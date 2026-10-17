using Spotlight.Core.Hidpp;

namespace Spotlight.Core;

public enum SpotlightState
{
    /// <summary>No receiver / Bluetooth Spotlight present.</summary>
    Searching,
    Connecting,
    /// <summary>Receiver present but the Spotlight does not answer (sleeping or out of range).</summary>
    Asleep,
    /// <summary>Top button diverted; pointer events are flowing.</summary>
    Ready,
    /// <summary>The device cannot divert the top button with motion.</summary>
    Unsupported,
    Error,
}

/// <summary>An opened HID++ transport and the product ID it belongs to.</summary>
public sealed record SpotlightConnection(IHidppTransport Transport, ushort ProductId);

/// <summary>
/// Keeps the Spotlight's top button (CID 0x00D8) diverted with raw XY, so holding it produces pointer events for
/// us instead of moving the Windows cursor, plus a few other controls (see <see cref="ExtraControls"/>) whose
/// presses are reported instead of performing their default action. Survives receiver unplug/replug and device
/// sleep: diversion is re-applied whenever the receiver reports the link coming back, the device asks for
/// reconfiguration, or a periodic check finds it gone.
/// </summary>
public sealed class SpotlightController : IAsyncDisposable
{
    public const ushort PointerControl = SpotlightIds.CursorButtonLongPress;

    /// <summary>Top-button click and Next/Back long presses: by default a slide-advancing click and two no-ops.</summary>
    public static readonly IReadOnlyList<ushort> DefaultExtraControls =
        [SpotlightIds.LeftButton, SpotlightIds.NextLongPress, SpotlightIds.BackLongPress];

    const byte ReceiverConnectionNotification = 0x41;
    const byte ReceiverNotificationRegister = 0x00;
    const byte WirelessNotificationsBit = 0x01; // bit 8 of the 24-bit register = bit 0 of its middle byte

    readonly Func<SpotlightConnection?> _connect;
    readonly CancellationTokenSource _stop = new();
    readonly SemaphoreSlim _wake = new(0);
    readonly Lock _buttonLock = new();
    Task? _run;
    HidppDevice? _device;
    ReprogControls? _reprog;
    byte? _wirelessStatusIndex;
    IReadOnlyList<ushort> _divertedExtras = [];
    HashSet<ushort> _heldExtras = [];
    volatile bool _connectionLost;
    volatile bool _reconfigureRequested;
    volatile bool _held;
    int _disposed;

    /// <param name="connect">Opens the Spotlight's transport; returns <c>null</c> when none is present.</param>
    public SpotlightController(Func<SpotlightConnection?> connect) => _connect = connect;

    public TimeSpan SearchInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan WakeRetryInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ReconfigureDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// Controls diverted (without raw XY) besides the pointer button. Their default action is suppressed and
    /// <see cref="ControlPressed"/> / <see cref="ControlReleased"/> are raised instead. Controls the device
    /// lacks are skipped.
    /// </summary>
    public IReadOnlyList<ushort> ExtraControls { get; init; } = DefaultExtraControls;

    public SpotlightState State { get; private set; } = SpotlightState.Searching;
    public string? StateDetail { get; private set; }

    /// <summary>State changes; raised on a background thread.</summary>
    public event Action<SpotlightState, string?>? StateChanged;

    // Pointer events are raised on HID reader threads, in device order. Keep handlers short.
    public event Action? PointerPressed;
    public event Action<int, int>? PointerMoved;
    public event Action? PointerReleased;
    public event Action<ushort>? ControlPressed;
    public event Action<ushort>? ControlReleased;

    public void Start() => _run ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>Stops and gives the top button back to the device (best effort).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        _wake.Release();
        if (_run is not null)
            await _run.ConfigureAwait(false);
        _stop.Dispose();
    }

    async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            SpotlightConnection? connection = null;
            try
            {
                connection = _connect();
                if (connection is null)
                    SetState(SpotlightState.Searching, null);
            }
            catch (Exception e)
            {
                SetState(SpotlightState.Error, e.Message);
            }

            if (connection is null)
            {
                await WaitForWakeAsync(SearchInterval, stop).ConfigureAwait(false);
                continue;
            }

            try
            {
                await RunConnectionAsync(connection, stop).ConfigureAwait(false);
            }
            finally
            {
                connection.Transport.Dispose();
            }
        }
    }

    async Task RunConnectionAsync(SpotlightConnection connection, CancellationToken stop)
    {
        _connectionLost = false;
        _reconfigureRequested = false;
        DrainWake();

        var device = new HidppDevice(connection.Transport, 0) { Timeout = RequestTimeout };
        var viaReceiver = connection.ProductId == SpotlightIds.ReceiverProductId;
        _device = device;
        connection.Transport.Failed += OnTransportFailed;
        device.Notification += OnNotification;
        SetState(SpotlightState.Connecting, null);

        byte[]? originalReceiverFlags = null;
        try
        {
            if (viaReceiver)
                originalReceiverFlags = await EnableReceiverNotificationsAsync(device).ConfigureAwait(false);

            var configured = false;
            while (!stop.IsCancellationRequested && !_connectionLost)
            {
                if (!configured || _reconfigureRequested)
                {
                    if (_reconfigureRequested)
                    {
                        _reconfigureRequested = false;
                        await Task.Delay(ReconfigureDelay, CancellationToken.None).ConfigureAwait(false);
                    }
                    configured = await TryConfigureAsync(device, viaReceiver).ConfigureAwait(false);
                }

                if (State == SpotlightState.Unsupported)
                {
                    await WaitForWakeAsync(Timeout.InfiniteTimeSpan, stop).ConfigureAwait(false);
                    continue;
                }

                var signaled = await WaitForWakeAsync(configured ? HealthCheckInterval : WakeRetryInterval, stop).ConfigureAwait(false);
                if (!signaled && configured && !stop.IsCancellationRequested)
                    configured = await IsStillDivertedAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            device.Notification -= OnNotification;
            connection.Transport.Failed -= OnTransportFailed;
            ReleaseAll();
            if (!_connectionLost)
                await RestoreAsync(device, originalReceiverFlags).ConfigureAwait(false);
            _device = null;
            _reprog = null;
            _wirelessStatusIndex = null;
            _divertedExtras = [];
        }
    }

    async Task<bool> TryConfigureAsync(HidppDevice device, bool viaReceiver)
    {
        try
        {
            if (device.DeviceIndex == 0)
                await FindDeviceIndexAsync(device, viaReceiver).ConfigureAwait(false);
            else
                await device.PingAsync().ConfigureAwait(false);

            if (_reprog is null)
            {
                var reprog = await ReprogControls.CreateAsync(device).ConfigureAwait(false);
                if (reprog is null)
                {
                    SetState(SpotlightState.Unsupported, "The device has no ReprogControlsV4 feature.");
                    return false;
                }

                var controls = await reprog.GetAllControlsAsync().ConfigureAwait(false);
                var pointer = controls.FirstOrDefault(c => c.Cid == PointerControl);
                if (pointer.Cid != PointerControl || !pointer.Flags.HasFlag(ControlFlags.Divertable) || !pointer.Flags.HasFlag(ControlFlags.RawXY))
                {
                    SetState(SpotlightState.Unsupported, $"The top button (0x{PointerControl:X4}) cannot be diverted with motion on this device.");
                    return false;
                }

                _divertedExtras = ExtraControls
                    .Where(cid => controls.Any(c => c.Cid == cid && c.Flags.HasFlag(ControlFlags.Divertable)))
                    .ToList();
                _wirelessStatusIndex = await device.GetFeatureIndexAsync(FeatureIds.WirelessDeviceStatus).ConfigureAwait(false);
                _reprog = reprog;
            }

            await _reprog.SetReportingAsync(PointerControl, divert: true, rawXY: true).ConfigureAwait(false);
            foreach (var cid in _divertedExtras)
                await _reprog.SetReportingAsync(cid, divert: true, rawXY: false).ConfigureAwait(false);
            SetState(SpotlightState.Ready, null);
            return true;
        }
        catch (Exception e) when (e is HidppException or TimeoutException or IOException)
        {
            SetState(SpotlightState.Asleep, "Press a button on the Spotlight to wake it.");
            return false;
        }
    }

    static async Task FindDeviceIndexAsync(HidppDevice device, bool viaReceiver)
    {
        byte[] candidates = viaReceiver ? [1, 2, 3, 4, 5, 6] : [HidppDevice.ReceiverIndex];
        Exception? last = null;
        foreach (var candidate in candidates)
        {
            device.DeviceIndex = candidate;
            try
            {
                await device.PingAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (e is HidppException or TimeoutException)
            {
                last = e;
            }
        }

        device.DeviceIndex = 0;
        throw last!;
    }

    /// <summary>Periodic safety net in case the device dropped the diversion without telling us.</summary>
    async Task<bool> IsStillDivertedAsync()
    {
        if (_reprog is not { } reprog)
            return false;
        try
        {
            var reporting = await reprog.GetReportingAsync(PointerControl).ConfigureAwait(false);
            return reporting.Flags.HasFlag(ReportingFlags.Diverted) && reporting.Flags.HasFlag(ReportingFlags.RawXYDiverted);
        }
        catch (Exception e) when (e is HidppException or TimeoutException or IOException)
        {
            return true; // asleep: nothing to check now, the wake-up notification will trigger a reconfigure
        }
    }

    static async Task<byte[]?> EnableReceiverNotificationsAsync(HidppDevice device)
    {
        try
        {
            var flags = await device.ReadReceiverRegisterAsync(ReceiverNotificationRegister).ConfigureAwait(false);
            if ((flags[1] & WirelessNotificationsBit) != 0)
                return null;
            await device.WriteReceiverRegisterAsync(ReceiverNotificationRegister,
                [flags[0], (byte)(flags[1] | WirelessNotificationsBit), flags[2]]).ConfigureAwait(false);
            return flags;
        }
        catch (Exception e) when (e is HidppException or TimeoutException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    async Task RestoreAsync(HidppDevice device, byte[]? originalReceiverFlags)
    {
        if (_reprog is { } reprog)
        {
            foreach (var cid in _divertedExtras.Prepend(PointerControl))
            {
                try
                {
                    await reprog.SetReportingAsync(cid, divert: false, rawXY: false).ConfigureAwait(false);
                }
                catch (Exception e) when (e is HidppException or TimeoutException or IOException)
                {
                    // Asleep or gone: the device drops non-persistent diversion when it resets anyway.
                    break;
                }
            }
        }

        try
        {
            if (originalReceiverFlags is not null)
                await device.WriteReceiverRegisterAsync(ReceiverNotificationRegister, originalReceiverFlags).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HidppException or TimeoutException or IOException)
        {
        }
    }

    // Runs on HID reader threads: parse, raise pointer events and signal the run loop - never send requests here.
    void OnNotification(HidppReport report)
    {
        if (_reprog is { } reprog)
        {
            if (reprog.TryParseDivertedButtons(report, out var held))
            {
                OnButtons(held);
                return;
            }
            if (reprog.TryParseRawXY(report, out var dx, out var dy))
            {
                if (_held)
                    PointerMoved?.Invoke(dx, dy);
                return;
            }
        }

        if (report.ReportId == HidppDevice.ShortReportId && report.FeatureIndex == ReceiverConnectionNotification
            && report.DeviceIndex != HidppDevice.ReceiverIndex)
        {
            var linkEstablished = (report.Data[4] & 0x40) == 0;
            if (linkEstablished)
                RequestReconfigure();
            else
                ReleaseAll();
            return;
        }

        if (_wirelessStatusIndex is { } statusIndex && report.DeviceIndex == _device?.DeviceIndex
            && report.FeatureIndex == statusIndex && report.Function == 0)
            RequestReconfigure();
    }

    void OnTransportFailed(Exception error)
    {
        _connectionLost = true;
        _wake.Release();
    }

    void RequestReconfigure()
    {
        _reconfigureRequested = true;
        _wake.Release();
    }

    /// <summary>Turns the "currently held diverted controls" list into press/release events.</summary>
    void OnButtons(IReadOnlyCollection<ushort> held)
    {
        bool? pointerDown = null;
        ushort[] pressed, released;
        lock (_buttonLock)
        {
            var pointerHeld = held.Contains(PointerControl);
            if (pointerHeld != _held)
            {
                _held = pointerHeld;
                pointerDown = pointerHeld;
            }

            var extras = held.Where(cid => cid != PointerControl).ToHashSet();
            pressed = [.. extras.Except(_heldExtras)];
            released = [.. _heldExtras.Except(extras)];
            _heldExtras = extras;
        }

        foreach (var cid in released)
            ControlReleased?.Invoke(cid);
        if (pointerDown is { } down)
            (down ? PointerPressed : PointerReleased)?.Invoke();
        foreach (var cid in pressed)
            ControlPressed?.Invoke(cid);
    }

    void ReleaseAll() => OnButtons([]);

    async Task<bool> WaitForWakeAsync(TimeSpan timeout, CancellationToken stop)
    {
        try
        {
            return await _wake.WaitAsync(timeout, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            DrainWake();
        }
    }

    void DrainWake()
    {
        while (_wake.Wait(0))
        {
        }
    }

    void SetState(SpotlightState state, string? detail)
    {
        if (State == state && StateDetail == detail)
            return;
        State = state;
        StateDetail = detail;
        StateChanged?.Invoke(state, detail);
    }
}
