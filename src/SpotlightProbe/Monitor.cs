using System.Runtime.InteropServices;
using Spotlight.Core;
using Spotlight.Core.Hidpp;

namespace SpotlightProbe;

/// <summary>
/// The key experiment: divert the top button (with raw XY) and check that presses, releases and motion reach us
/// over HID++ while the Windows cursor stays where it is.
/// </summary>
sealed class Monitor(Options options)
{
    const byte ReceiverConnectionNotification = 0x41;
    const byte ReceiverNotificationRegister = 0x00;
    const byte WirelessNotificationsBit = 0x01; // bit 8 of the 24-bit register, i.e. bit 0 of its middle byte

    readonly Lock _lock = new();
    readonly Dictionary<ushort, Hold> _holds = [];
    Session _session = null!;
    ReprogControls _reprog = null!;
    IReadOnlyList<(ushort Cid, bool RawXY)> _targets = [];
    byte? _wirelessStatusIndex;
    byte[]? _originalReceiverNotifications;
    HashSet<ushort> _held = [];
    int _dx, _dy, _events; // motion since the last printed line
    long _reapplyAtMs = -1;
    volatile bool _quit;

    sealed class Hold
    {
        public long StartMs;
        public (int X, int Y) CursorAtStart;
        public int Dx, Dy, Events;
    }

    public async Task<int> RunAsync()
    {
        using var session = await Session.ConnectAsync(options);
        _session = session;
        Log.Info($"Connected: {session.Description}, HID++ {session.ProtocolMajor}.{session.ProtocolMinor}");

        _reprog = await ReprogControls.CreateAsync(session.Device)
                  ?? throw new ProbeException("This device has no ReprogControlsV4 (0x1B04) feature, so buttons cannot be diverted.");
        _targets = await ChooseTargetsAsync();
        _wirelessStatusIndex = await session.Device.GetFeatureIndexAsync(FeatureIds.WirelessDeviceStatus);
        await EnableReceiverNotificationsAsync();

        session.Device.Notification += OnNotification;
        Console.CancelKeyPress += OnCancelKeyPress;
        try
        {
            await ApplyDiversionAsync();
            Log.Info("Hold the top button and move the Spotlight around. Keys: [r] re-apply diversion   [q] quit");
            await LoopAsync();
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            session.Device.Notification -= OnNotification;
            await RestoreAsync();
        }
        return 0;
    }

    async Task<IReadOnlyList<(ushort, bool)>> ChooseTargetsAsync()
    {
        var controls = (await _reprog.GetAllControlsAsync()).ToDictionary(c => c.Cid);
        IEnumerable<ushort> wanted = options.AllCids ? controls.Values.Where(c => c.Flags.HasFlag(ControlFlags.Divertable)).Select(c => c.Cid)
            : options.Cids.Count > 0 ? options.Cids
            : [SpotlightIds.CursorButtonLongPress];

        var targets = new List<(ushort, bool)>();
        foreach (var cid in wanted)
        {
            if (!controls.TryGetValue(cid, out var control))
                Log.Warn($"0x{cid:X4} is not a control of this device - skipped. 'spotlight-probe controls' lists them.");
            else if (!control.Flags.HasFlag(ControlFlags.Divertable))
                Log.Warn($"0x{cid:X4} {SpotlightIds.ControlName(cid)} cannot be diverted - skipped.");
            else
                targets.Add((cid, !options.NoRawXY && control.Flags.HasFlag(ControlFlags.RawXY)));
        }

        if (targets.Count == 0)
            throw new ProbeException("Nothing to divert.");
        return targets;
    }

    /// <summary>
    /// Asks the receiver to report link changes (HID++ 1.0 notification 0x41), so we notice the Spotlight waking up
    /// and can re-apply the diversion in case the device dropped it while asleep.
    /// </summary>
    async Task EnableReceiverNotificationsAsync()
    {
        if (!_session.ViaReceiver || !_session.Transport.SupportsShort)
            return;
        try
        {
            var flags = await _session.Device.ReadReceiverRegisterAsync(ReceiverNotificationRegister);
            if ((flags[1] & WirelessNotificationsBit) != 0)
                return;
            await _session.Device.WriteReceiverRegisterAsync(ReceiverNotificationRegister,
                [flags[0], (byte)(flags[1] | WirelessNotificationsBit), flags[2]]);
            _originalReceiverNotifications = flags;
            Log.Detail("Turned on receiver connection notifications");
        }
        catch (Exception e) when (e is HidppException or TimeoutException)
        {
            Log.Warn($"Could not turn on receiver connection notifications: {e.Message}");
        }
    }

    async Task ApplyDiversionAsync()
    {
        foreach (var (cid, rawXY) in _targets)
        {
            try
            {
                await _reprog.SetReportingAsync(cid, divert: true, rawXY);
                var state = await _reprog.GetReportingAsync(cid);
                var asked = state.Flags.HasFlag(ReportingFlags.Diverted) && (!rawXY || state.Flags.HasFlag(ReportingFlags.RawXYDiverted));
                var text = $"Diverted 0x{cid:X4} {SpotlightIds.ControlName(cid)}{(rawXY ? " with raw XY" : "")} - device reports: {state.Flags}";
                if (asked)
                    Log.Good(text);
                else
                    Log.Warn(text + " (not what we asked for)");
            }
            catch (Exception e) when (e is HidppException or TimeoutException)
            {
                Log.Warn($"Could not divert 0x{cid:X4}: {e.Message} - wake the Spotlight and press [r].");
            }
        }
    }

    async Task RestoreAsync()
    {
        foreach (var (cid, _) in _targets)
        {
            try
            {
                await _reprog.SetReportingAsync(cid, divert: false, rawXY: false);
                Log.Info($"Restored 0x{cid:X4} {SpotlightIds.ControlName(cid)}");
            }
            catch (Exception e) when (e is HidppException or TimeoutException)
            {
                Log.Warn($"Could not restore 0x{cid:X4} ({e.Message}). If that button stays dead, run 'spotlight-probe restore'.");
            }
        }

        if (_originalReceiverNotifications is { } original)
        {
            try
            {
                await _session.Device.WriteReceiverRegisterAsync(ReceiverNotificationRegister, original);
            }
            catch (Exception e) when (e is HidppException or TimeoutException)
            {
                Log.Detail($"Could not reset receiver notifications: {e.Message}");
            }
        }
    }

    async Task LoopAsync()
    {
        long lastPrint = 0;
        while (!_quit)
        {
            HandleKeys();
            if (TakeDueReapply())
                await ApplyDiversionAsync();
            if (Log.ElapsedMs - lastPrint >= 100)
            {
                lock (_lock)
                    PrintMotion();
                lastPrint = Log.ElapsedMs;
            }
            await Task.Delay(15);
        }
    }

    void HandleKeys()
    {
        if (Console.IsInputRedirected)
            return;
        while (Console.KeyAvailable)
        {
            switch (Console.ReadKey(intercept: true).Key)
            {
                case ConsoleKey.Q or ConsoleKey.Escape:
                    _quit = true;
                    break;
                case ConsoleKey.R:
                    ScheduleReapply(0);
                    break;
            }
        }
    }

    void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true; // let the finally block restore the buttons
        _quit = true;
    }

    void ScheduleReapply(int delayMs)
    {
        lock (_lock)
            _reapplyAtMs = Log.ElapsedMs + delayMs;
    }

    bool TakeDueReapply()
    {
        lock (_lock)
        {
            if (_reapplyAtMs < 0 || Log.ElapsedMs < _reapplyAtMs)
                return false;
            _reapplyAtMs = -1;
            return true;
        }
    }

    // Runs on HID reader threads: only record and print here; requests are sent from the main loop.
    void OnNotification(HidppReport report)
    {
        if (_reprog.TryParseDivertedButtons(report, out var held))
        {
            OnButtons(held);
            return;
        }
        if (_reprog.TryParseRawXY(report, out var dx, out var dy))
        {
            OnMotion(dx, dy);
            return;
        }

        var deviceIndex = _session.Device.DeviceIndex;
        if (report.ReportId == HidppDevice.ShortReportId && report.FeatureIndex == ReceiverConnectionNotification
            && report.DeviceIndex == deviceIndex)
        {
            var linked = (report.Data[4] & 0x40) == 0;
            Log.Event($"Receiver: Spotlight {(linked ? "connected" : "disconnected (asleep or out of range)")}  [{report}]");
            if (linked)
                ScheduleReapply(300);
            return;
        }

        if (report.DeviceIndex == deviceIndex && report.FeatureIndex == _wirelessStatusIndex && report.Function == 0)
        {
            Log.Event($"Spotlight: wireless status {Hex.Format(report.Params[..3])} (first byte 1 = reconnected)");
            ScheduleReapply(300);
            return;
        }

        var source = report.DeviceIndex == deviceIndex ? _session.FeatureName(report.FeatureIndex) : $"device 0x{report.DeviceIndex:X2}";
        Log.Detail($"Other notification from {source}: {report}");
    }

    void OnButtons(ushort[] held)
    {
        var now = Log.ElapsedMs;
        var cursor = CursorPosition();
        lock (_lock)
        {
            var next = held.ToHashSet();
            foreach (var cid in next.Except(_held))
            {
                _holds[cid] = new Hold { StartMs = now, CursorAtStart = cursor };
                Log.Event($"PRESS    0x{cid:X4} {SpotlightIds.ControlName(cid)}   (Windows cursor at {cursor.X},{cursor.Y})");
            }

            foreach (var cid in _held.Except(next))
            {
                PrintMotion();
                if (!_holds.Remove(cid, out var hold))
                {
                    Log.Event($"RELEASE  0x{cid:X4} {SpotlightIds.ControlName(cid)}");
                    continue;
                }

                Log.Event($"RELEASE  0x{cid:X4} {SpotlightIds.ControlName(cid)} after {(now - hold.StartMs) / 1000.0:0.00} s - " +
                          $"{hold.Events} motion events, total dx={hold.Dx:+0;-0;0} dy={hold.Dy:+0;-0;0}");
                if (hold.Events == 0)
                    continue;
                if (cursor == hold.CursorAtStart)
                    Log.Good("Windows cursor did not move - the motion went only to this tool.");
                else
                    Log.Warn($"Windows cursor moved from {hold.CursorAtStart.X},{hold.CursorAtStart.Y} to {cursor.X},{cursor.Y} during the hold " +
                             "(motion also reached Windows - or the mouse was moved).");
            }

            _held = next;
        }
    }

    void OnMotion(short dx, short dy)
    {
        lock (_lock)
        {
            _dx += dx;
            _dy += dy;
            _events++;
            foreach (var hold in _holds.Values)
            {
                hold.Dx += dx;
                hold.Dy += dy;
                hold.Events++;
            }
        }
    }

    /// <summary>Prints motion collected since the last call. Caller holds <see cref="_lock"/>.</summary>
    void PrintMotion()
    {
        if (_events == 0)
            return;
        var cursor = CursorPosition();
        Log.Detail($"  motion dx={_dx,5:+0;-0;0} dy={_dy,5:+0;-0;0}  ({_events,2} events)   Windows cursor {cursor.X},{cursor.Y}");
        _dx = _dy = _events = 0;
    }

    static (int X, int Y) CursorPosition() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT point);
}
