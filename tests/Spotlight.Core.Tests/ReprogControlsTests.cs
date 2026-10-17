using Spotlight.Core.Hidpp;

namespace Spotlight.Core.Tests;

public class ReprogControlsTests
{
    const byte ReprogIndex = 0x09;

    readonly FakeTransport _transport = new();
    readonly HidppDevice _device;

    public ReprogControlsTests()
    {
        _device = new HidppDevice(_transport, deviceIndex: 1) { Timeout = TimeSpan.FromMilliseconds(200) };
        _transport.Responder = Respond;
    }

    // Behaves like a Spotlight with three controls: D8 (divertable, raw XY), DA and DC (divertable).
    byte[]? Respond(byte[] r)
    {
        if (r[2] == 0x00) // root getFeature
            return FakeTransport.Reply(r, r[4] == 0x1B && r[5] == 0x04 ? ReprogIndex : (byte)0);
        if (r[2] != ReprogIndex)
            return null;
        return (r[3] >> 4) switch
        {
            0 => FakeTransport.Reply(r, 3),
            1 => r[4] switch
            {
                0 => FakeTransport.Reply(r, 0x00, 0xD8, 0x00, 0xA6, 0x30, 0, 1, 0, 0x01),
                1 => FakeTransport.Reply(r, 0x00, 0xDA, 0x00, 0xA7, 0x30, 1, 1, 0, 0x00),
                _ => FakeTransport.Reply(r, 0x00, 0xDC, 0x00, 0xA8, 0x30, 2, 1, 0, 0x00),
            },
            2 => FakeTransport.Reply(r, r[4], r[5], 0x11, 0, 0),
            3 => FakeTransport.Reply(r, r[4], r[5], r[6]),
            _ => null,
        };
    }

    async Task<ReprogControls> CreateAsync() => Assert.IsType<ReprogControls>(await ReprogControls.CreateAsync(_device));

    [Fact]
    public async Task Lists_controls_with_combined_flags()
    {
        var controls = await (await CreateAsync()).GetAllControlsAsync();

        Assert.Equal(new ushort[] { 0xD8, 0xDA, 0xDC }, controls.Select(c => c.Cid));
        Assert.Equal(ControlFlags.Reprogrammable | ControlFlags.Divertable | ControlFlags.RawXY, controls[0].Flags);
        Assert.Equal(ControlFlags.Reprogrammable | ControlFlags.Divertable, controls[1].Flags);
        Assert.Equal((ushort)0xA6, controls[0].TaskId);
    }

    [Fact]
    public async Task Divert_with_raw_xy_sends_0x33_and_restore_sends_0x22()
    {
        var reprog = await CreateAsync();
        _transport.Sent.Clear();

        await reprog.SetReportingAsync(0xD8, divert: true, rawXY: true);
        await reprog.SetReportingAsync(0xD8, divert: false, rawXY: false);

        Assert.Equal(new byte[] { 0x11, 0x01, ReprogIndex, 0x3A, 0x00, 0xD8, 0x33, 0, 0 }, _transport.Sent[0][..9]);
        Assert.Equal(0x22, _transport.Sent[1][6]);
    }

    [Theory]
    [InlineData(true, false, null, 0x23)]
    [InlineData(false, false, false, 0x2A)]
    [InlineData(true, true, true, 0x3F)]
    public void Reporting_flags_pair_each_setting_with_its_valid_bit(bool divert, bool rawXY, bool? persist, byte expected)
        => Assert.Equal(expected, ReprogControls.BuildReportingFlags(divert, rawXY, persist));

    [Fact]
    public async Task Reads_reporting_state()
    {
        var reporting = await (await CreateAsync()).GetReportingAsync(0xD8);

        Assert.Equal(ReportingFlags.Diverted | ReportingFlags.RawXYDiverted, reporting.Flags);
    }

    [Fact]
    public async Task Parses_diverted_button_events()
    {
        var reprog = await CreateAsync();
        var pressed = Event(0, 0x00, 0xD8, 0x00, 0xDA);
        var released = Event(0);

        Assert.True(reprog.TryParseDivertedButtons(pressed, out var held));
        Assert.Equal(new ushort[] { 0xD8, 0xDA }, held);
        Assert.True(reprog.TryParseDivertedButtons(released, out held));
        Assert.Empty(held);
        Assert.False(reprog.TryParseRawXY(pressed, out _, out _));
    }

    [Fact]
    public async Task Parses_signed_raw_xy_deltas()
    {
        var reprog = await CreateAsync();

        Assert.True(reprog.TryParseRawXY(Event(1, 0xFF, 0xFD, 0x00, 0x0C), out var dx, out var dy));
        Assert.Equal(-3, dx);
        Assert.Equal(12, dy);
    }

    [Fact]
    public async Task Ignores_replies_and_other_devices()
    {
        var reprog = await CreateAsync();
        var reply = Event(1, 0, 1, 0, 1);
        reply.Data[3] |= 0x0A; // software ID set: a late reply, not an event
        var otherDevice = Event(1, 0, 1, 0, 1);
        otherDevice.Data[1] = 0x02;

        Assert.False(reprog.TryParseRawXY(reply, out _, out _));
        Assert.False(reprog.TryParseRawXY(otherDevice, out _, out _));
    }

    static HidppReport Event(int function, params byte[] parameters)
    {
        var data = new byte[HidppDevice.LongReportLength];
        data[0] = HidppDevice.LongReportId;
        data[1] = 0x01;
        data[2] = ReprogIndex;
        data[3] = (byte)(function << 4);
        parameters.CopyTo(data, 4);
        return new HidppReport(data);
    }
}
