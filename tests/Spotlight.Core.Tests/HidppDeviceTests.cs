using Spotlight.Core.Hidpp;

namespace Spotlight.Core.Tests;

public class HidppDeviceTests
{
    readonly FakeTransport _transport = new();
    readonly HidppDevice _device;

    public HidppDeviceTests() => _device = new HidppDevice(_transport, deviceIndex: 1) { Timeout = TimeSpan.FromMilliseconds(200) };

    [Fact]
    public async Task Call_sends_long_report_with_function_and_software_id()
    {
        _transport.Responder = r => FakeTransport.Reply(r, 0x42);

        var reply = await _device.CallAsync(0x05, 3, 0x00, 0xD8, 0x33);

        var sent = Assert.Single(_transport.Sent);
        Assert.Equal(20, sent.Length);
        Assert.Equal(new byte[] { 0x11, 0x01, 0x05, 0x3A, 0x00, 0xD8, 0x33 }, sent[..7]);
        Assert.Equal(0x42, reply.Params[0]);
    }

    [Fact]
    public async Task Call_uses_short_report_when_long_is_unavailable()
    {
        _transport.SupportsLong = false;
        _transport.Responder = r => FakeTransport.Reply(r);

        await _device.CallAsync(0x00, 1, 0, 0, 0x5A);

        Assert.Equal(new byte[] { 0x10, 0x01, 0x00, 0x1A, 0x00, 0x00, 0x5A }, Assert.Single(_transport.Sent));
    }

    [Fact]
    public async Task Hidpp20_error_reply_throws_with_code()
    {
        _transport.Responder = r => [0x11, 0x01, 0xFF, r[2], r[3], 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        var e = await Assert.ThrowsAsync<HidppException>(() => _device.CallAsync(0x05, 1));

        Assert.Equal(HidppProtocol.Hidpp20, e.Protocol);
        Assert.Equal(0x02, e.Code);
    }

    [Fact]
    public async Task Receiver_error_for_sleeping_device_is_unreachable()
    {
        _transport.Responder = r => [0x10, 0x01, 0x8F, r[2], r[3], 0x09, 0x00];

        var e = await Assert.ThrowsAsync<HidppException>(() => _device.PingAsync());

        Assert.Equal(HidppProtocol.Hidpp10, e.Protocol);
        Assert.True(e.IsDeviceUnreachable);
    }

    [Fact]
    public async Task Ping_returns_protocol_version()
    {
        _transport.Responder = r => FakeTransport.Reply(r, 4, 5, r[6]);

        Assert.Equal((4, 5), await _device.PingAsync());
    }

    [Fact]
    public async Task Ping_of_hidpp10_device_reports_version_1()
    {
        _transport.Responder = r => [0x10, 0x01, 0x8F, r[2], r[3], 0x01, 0x00];

        Assert.Equal((1, 0), await _device.PingAsync());
    }

    [Fact]
    public async Task Missing_reply_times_out()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => _device.CallAsync(0x05, 0));
    }

    [Fact]
    public async Task Reply_for_another_device_index_is_not_taken_as_answer()
    {
        _transport.Responder = r => { var reply = FakeTransport.Reply(r); reply[1] = 0x02; return reply; };
        var notifications = new List<HidppReport>();
        _device.Notification += notifications.Add;

        await Assert.ThrowsAsync<TimeoutException>(() => _device.CallAsync(0x05, 0));
        Assert.Single(notifications);
    }

    [Fact]
    public async Task Notifications_during_a_request_are_raised_and_do_not_complete_it()
    {
        var notifications = new List<HidppReport>();
        _device.Notification += notifications.Add;
        _transport.Responder = r =>
        {
            // A button event (software ID 0) arrives before the actual reply.
            _transport.Inject(0x11, 0x01, r[2], 0x00, 0x00, 0xD8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            return FakeTransport.Reply(r, 0x07);
        };

        var reply = await _device.CallAsync(0x05, 0);

        Assert.Equal(0x07, reply.Params[0]);
        var notification = Assert.Single(notifications);
        Assert.Equal(0, notification.SoftwareId);
    }

    [Fact]
    public async Task Feature_index_is_looked_up_once_and_cached()
    {
        _transport.Responder = r => FakeTransport.Reply(r, r[4] == 0x1B && r[5] == 0x04 ? (byte)0x09 : (byte)0x00);

        Assert.Equal((byte)0x09, await _device.GetFeatureIndexAsync(FeatureIds.ReprogControlsV4));
        Assert.Equal((byte)0x09, await _device.GetFeatureIndexAsync(FeatureIds.ReprogControlsV4));
        Assert.Null(await _device.GetFeatureIndexAsync(FeatureIds.Haptic));
        Assert.Equal(2, _transport.Sent.Count);
    }

    [Fact]
    public async Task Receiver_register_read_uses_short_report_to_receiver()
    {
        _transport.Responder = r => [0x10, 0xFF, 0x81, 0x00, 0x00, 0x09, 0x00];

        var value = await _device.ReadReceiverRegisterAsync(0x00);

        Assert.Equal(new byte[] { 0x10, 0xFF, 0x81, 0x00, 0, 0, 0 }, Assert.Single(_transport.Sent));
        Assert.Equal(new byte[] { 0x00, 0x09, 0x00 }, value);
    }
}
