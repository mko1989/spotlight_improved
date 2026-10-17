using Spotlight.Core.Hidpp;

namespace Spotlight.Core.Tests;

/// <summary>A Spotlight behind its receiver (device index 1), simulated at the HID++ report level.</summary>
sealed class SimulatedSpotlight
{
    public const byte ReprogIndex = 0x09;
    public const byte WirelessStatusIndex = 0x04;

    // The controls a real Spotlight reports (Solaar issue #2881): all nonstandard + reprogrammable + divertable,
    // D8 / DA / DC also raw XY.
    static readonly (ushort Cid, bool RawXY)[] RealControls =
        [(0x50, false), (0xD8, true), (0xDF, false), (0xD9, false), (0xDA, true), (0xDB, false), (0xDC, true)];

    readonly Lock _lock = new();
    public FakeTransport Transport { get; private set; } = null!;
    public volatile bool Asleep;
    public bool PointerSupportsRawXY { get; init; } = true;
    public ushort[] MissingControls { get; init; } = [];
    public Dictionary<ushort, byte> ReportingFlags { get; } = [];
    public List<(ushort Cid, byte Flags)> SetReporting { get; } = [];
    public int Connects { get; private set; }

    (ushort Cid, bool RawXY)[] Controls => RealControls
        .Where(c => !MissingControls.Contains(c.Cid))
        .Select(c => c.Cid == 0xD8 ? (c.Cid, PointerSupportsRawXY) : c)
        .ToArray();

    public byte Flags(ushort cid)
    {
        lock (_lock)
            return ReportingFlags.GetValueOrDefault(cid);
    }

    public (ushort Cid, byte Flags)[] SetReportingCalls()
    {
        lock (_lock)
            return [.. SetReporting];
    }

    public SpotlightConnection Connect()
    {
        lock (_lock)
        {
            Connects++;
            Transport = new FakeTransport { Responder = Respond };
            return new SpotlightConnection(Transport, SpotlightIds.ReceiverProductId);
        }
    }

    byte[]? Respond(byte[] r)
    {
        if (r[1] == HidppDevice.ReceiverIndex) // receiver register access: echo, register value 0
            return [0x10, 0xFF, r[2], r[3], 0, 0, 0];
        if (r[1] != 1)
            return [0x10, r[1], 0x8F, r[2], r[3], 0x08, 0]; // unknown device
        if (Asleep)
            return [0x10, 0x01, 0x8F, r[2], r[3], 0x09, 0]; // resource error

        var function = r[3] >> 4;
        switch (r[2])
        {
            case 0x00 when function == 1:
                return FakeTransport.Reply(r, 4, 5, r[6]);
            case 0x00:
                var id = r[4] << 8 | r[5];
                return FakeTransport.Reply(r, id switch
                {
                    FeatureIds.ReprogControlsV4 => ReprogIndex,
                    FeatureIds.WirelessDeviceStatus => WirelessStatusIndex,
                    _ => 0,
                });
            case ReprogIndex when function == 0:
                return FakeTransport.Reply(r, (byte)Controls.Length);
            case ReprogIndex when function == 1:
                var (cid, rawXY) = Controls[r[4]];
                return FakeTransport.Reply(r, 0x00, (byte)cid, 0x00, 0xA0, 0x34, 0, 0, 0, (byte)(rawXY ? 0x01 : 0x00));
            case ReprogIndex when function == 2:
                return FakeTransport.Reply(r, r[4], r[5], Flags((ushort)(r[4] << 8 | r[5])));
            case ReprogIndex when function == 3:
                lock (_lock)
                {
                    var target = (ushort)(r[4] << 8 | r[5]);
                    SetReporting.Add((target, r[6]));
                    ReportingFlags[target] = (byte)(r[6] & 0x11); // keep the divert / raw XY state bits
                }
                return FakeTransport.Reply(r, r[4], r[5], r[6]);
            default:
                return [0x11, 0x01, 0xFF, r[2], r[3], 0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        }
    }

    public void Buttons(params ushort[] held)
    {
        var report = Event(ReprogIndex, 0);
        for (var i = 0; i < held.Length; i++)
        {
            report[4 + i * 2] = (byte)(held[i] >> 8);
            report[5 + i * 2] = (byte)held[i];
        }
        Transport.Inject(report);
    }

    public void Motion(short dx, short dy)
    {
        var report = Event(ReprogIndex, 1);
        report[4] = (byte)(dx >> 8);
        report[5] = (byte)dx;
        report[6] = (byte)(dy >> 8);
        report[7] = (byte)dy;
        Transport.Inject(report);
    }

    /// <summary>Receiver notification 0x41: link established or lost.</summary>
    public void Link(bool established) => Transport.Inject(0x10, 0x01, 0x41, 0x04, (byte)(established ? 0x00 : 0x40), 0x1A, 0xB0);

    static byte[] Event(byte featureIndex, int function)
    {
        var report = new byte[HidppDevice.LongReportLength];
        report[0] = HidppDevice.LongReportId;
        report[1] = 0x01;
        report[2] = featureIndex;
        report[3] = (byte)(function << 4);
        return report;
    }
}

public class SpotlightControllerTests : IAsyncLifetime
{
    readonly SimulatedSpotlight _spotlight = new();
    SpotlightController _controller = null!;
    readonly List<string> _events = [];

    public Task InitializeAsync()
    {
        _controller = Create(_spotlight);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _controller.DisposeAsync();

    SpotlightController Create(SimulatedSpotlight spotlight)
    {
        var controller = new SpotlightController(spotlight.Connect)
        {
            SearchInterval = TimeSpan.FromMilliseconds(20),
            WakeRetryInterval = TimeSpan.FromMilliseconds(50),
            ReconfigureDelay = TimeSpan.Zero,
            RequestTimeout = TimeSpan.FromMilliseconds(200),
        };
        controller.PointerPressed += () => Record("down");
        controller.PointerMoved += (dx, dy) => Record($"move {dx},{dy}");
        controller.PointerReleased += () => Record("up");
        controller.ControlPressed += cid => Record($"press {cid:X2}");
        controller.ControlReleased += cid => Record($"release {cid:X2}");
        return controller;
    }

    void Record(string e)
    {
        lock (_events)
            _events.Add(e);
    }

    string[] Events()
    {
        lock (_events)
            return [.. _events];
    }

    static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), $"Timed out waiting for: {what}");
    }

    [Fact]
    public async Task Diverts_pointer_with_raw_xy_and_click_and_long_presses_without()
    {
        _controller.Start();

        await WaitUntil(() => _controller.State == SpotlightState.Ready, "ready");
        Assert.Equal([(0xD8, 0x33), (0x50, 0x23), (0xDA, 0x23), (0xDC, 0x23)], _spotlight.SetReportingCalls());
    }

    [Fact]
    public async Task Short_presses_of_next_and_back_are_left_alone()
    {
        _controller.Start();
        await WaitUntil(() => _controller.State == SpotlightState.Ready, "ready");

        Assert.DoesNotContain(_spotlight.SetReportingCalls(), call => call.Cid is 0xD9 or 0xDB or 0xDF);
    }

    [Fact]
    public async Task Click_and_long_presses_raise_control_events_not_pointer_events()
    {
        _controller.Start();
        await WaitUntil(() => _controller.State == SpotlightState.Ready, "ready");

        _spotlight.Buttons(0x50);
        _spotlight.Buttons();
        _spotlight.Buttons(0xDA);
        _spotlight.Buttons();
        _spotlight.Buttons(0xDC);
        _spotlight.Buttons();

        Assert.Equal(["press 50", "release 50", "press DA", "release DA", "press DC", "release DC"], Events());
    }

    [Fact]
    public async Task Pointer_and_long_press_held_together_are_tracked_separately()
    {
        _controller.Start();
        await WaitUntil(() => _controller.State == SpotlightState.Ready, "ready");

        _spotlight.Buttons(0xD8);
        _spotlight.Buttons(0xD8, 0xDA);
        _spotlight.Buttons(0xDA);
        _spotlight.Buttons();

        Assert.Equal(["down", "press DA", "up", "release DA"], Events());
    }

    [Fact]
    public async Task Skips_extra_controls_the_device_lacks()
    {
        var spotlight = new SimulatedSpotlight { MissingControls = [0x50, 0xDC] };
        await using var controller = Create(spotlight);
        controller.Start();

        await WaitUntil(() => controller.State == SpotlightState.Ready, "ready");
        Assert.Equal([(0xD8, 0x33), (0xDA, 0x23)], spotlight.SetReportingCalls());
    }

    [Fact]
    public async Task Raises_press_motion_and_release()
    {
        _controller.Start();
        await WaitUntil(() => _controller.State == SpotlightState.Ready, "ready");

        _spotlight.Motion(5, 5); // not held: ignored
        _spotlight.Buttons(0xD8);
        _spotlight.Motion(3, -2);
        _spotlight.Motion(-1, 4);
        _spotlight.Buttons();

        Assert.Equal(["down", "move 3,-2", "move -1,4", "up"], Events());
    }

    [Fact]
    public async Task Reapplies_diversion_when_link_comes_back()
    {
        _controller.Start();
        await WaitUntil(() => _controller.State == SpotlightState.Ready, "ready");

        _spotlight.Link(established: false);
        _spotlight.Link(established: true);

        await WaitUntil(() => _spotlight.SetReportingCalls().Count(c => c.Cid == 0xD8) == 2, "second diversion");
    }

    [Fact]
    public async Task Link_loss_while_held_releases_the_pointer()
    {
        _controller.Start();
        await WaitUntil(() => _controller.State == SpotlightState.Ready, "ready");

        _spotlight.Buttons(0xD8, 0xDA);
        _spotlight.Link(established: false);

        Assert.Equal(["down", "press DA", "release DA", "up"], Events());
    }

    [Fact]
    public async Task Waits_for_a_sleeping_device_and_configures_it_on_wake()
    {
        _spotlight.Asleep = true;
        _controller.Start();
        await WaitUntil(() => _controller.State == SpotlightState.Asleep, "asleep");

        _spotlight.Asleep = false;
        _spotlight.Link(established: true);

        await WaitUntil(() => _controller.State == SpotlightState.Ready, "ready after wake");
    }

    [Fact]
    public async Task Reconnects_after_the_receiver_is_unplugged()
    {
        _controller.Start();
        await WaitUntil(() => _controller.State == SpotlightState.Ready, "ready");
        _spotlight.Buttons(0xD8);
        var first = _spotlight.Transport;

        first.Fail();

        await WaitUntil(() => _spotlight.Connects == 2 && _controller.State == SpotlightState.Ready, "reconnected");
        Assert.True(first.Disposed);
        Assert.Equal(["down", "up"], Events());
    }

    [Fact]
    public async Task Restores_the_button_on_dispose()
    {
        _controller.Start();
        await WaitUntil(() => _controller.State == SpotlightState.Ready, "ready");

        await _controller.DisposeAsync();

        Assert.Equal([(0xD8, 0x22), (0x50, 0x22), (0xDA, 0x22), (0xDC, 0x22)], _spotlight.SetReportingCalls()[^4..]);
        Assert.All(new ushort[] { 0xD8, 0x50, 0xDA, 0xDC }, cid => Assert.Equal(0, _spotlight.Flags(cid)));
    }

    [Fact]
    public async Task Reports_unsupported_when_top_button_has_no_raw_xy()
    {
        await using var controller = Create(new SimulatedSpotlight { PointerSupportsRawXY = false });
        controller.Start();

        await WaitUntil(() => controller.State == SpotlightState.Unsupported, "unsupported");
    }
}
