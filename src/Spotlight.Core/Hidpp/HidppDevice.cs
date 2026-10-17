namespace Spotlight.Core.Hidpp;

/// <summary>
/// Request/response and notification handling for one HID++ device (one device index behind a transport).
/// Requests are sent one at a time; anything that is not the reply to the outstanding request is raised
/// as a <see cref="Notification"/>.
/// </summary>
public sealed class HidppDevice
{
    public const byte ShortReportId = 0x10;
    public const byte LongReportId = 0x11;
    public const int ShortReportLength = 7;
    public const int LongReportLength = 20;

    /// <summary>Device index of the receiver itself, and of a device connected directly (Bluetooth).</summary>
    public const byte ReceiverIndex = 0xFF;

    // Any non-zero value works: devices send their notifications with software ID 0, so a non-zero ID tells
    // our replies apart from unsolicited events.
    const byte SoftwareId = 0x0A;

    readonly IHidppTransport _transport;
    readonly SemaphoreSlim _requestLock = new(1, 1);
    readonly Lock _pendingLock = new();
    readonly Dictionary<ushort, byte> _featureIndices = [];
    PendingRequest? _pending;

    public HidppDevice(IHidppTransport transport, byte deviceIndex)
    {
        _transport = transport;
        DeviceIndex = deviceIndex;
        transport.ReportReceived += OnReport;
    }

    public byte DeviceIndex { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Unsolicited reports (button events, connection changes, ...). Raised on transport threads: handlers
    /// must not block waiting for a reply from this device, or they would stall the thread that delivers it.
    /// </summary>
    public event Action<HidppReport>? Notification;

    /// <summary>Every report sent (<c>true</c>) or received (<c>false</c>), for diagnostics.</summary>
    public event Action<HidppReport, bool>? Traffic;

    /// <summary>Calls a function of a HID++ 2.0 feature.</summary>
    public Task<HidppReport> CallAsync(byte featureIndex, byte function, params byte[] args)
        => CallAsync(featureIndex, function, args, null);

    public Task<HidppReport> CallAsync(byte featureIndex, byte function, byte[] args, TimeSpan? timeout)
    {
        // HID++ 2.0 devices accept long reports for every request, so use them whenever we can.
        var reportId = _transport.SupportsLong ? LongReportId : ShortReportId;
        var address = (byte)((function << 4) | SoftwareId);
        return RequestAsync(Build(reportId, DeviceIndex, featureIndex, address, args), timeout);
    }

    /// <summary>
    /// HID++ 2.0 ping (root feature, function 1). Returns the protocol version; a HID++ 1.0 device answers
    /// with "invalid sub ID", which is reported as version 1.0.
    /// </summary>
    public async Task<(int Major, int Minor)> PingAsync(TimeSpan? timeout = null)
    {
        const byte marker = 0x5A;
        try
        {
            var reply = await CallAsync(0, 1, [0, 0, marker], timeout).ConfigureAwait(false);
            return (reply.Params[0], reply.Params[1]);
        }
        catch (HidppException e) when (e.Protocol == HidppProtocol.Hidpp10 && e.Code == 0x01)
        {
            return (1, 0);
        }
    }

    /// <summary>Looks up (and caches) the index of a feature; <c>null</c> when the device does not have it.</summary>
    public async Task<byte?> GetFeatureIndexAsync(ushort featureId)
    {
        if (featureId == FeatureIds.Root)
            return 0;

        lock (_featureIndices)
        {
            if (_featureIndices.TryGetValue(featureId, out var cached))
                return cached == 0 ? null : cached;
        }

        var reply = await CallAsync(0, 0, (byte)(featureId >> 8), (byte)featureId).ConfigureAwait(false);
        var index = reply.Params[0];
        lock (_featureIndices)
            _featureIndices[featureId] = index;
        return index == 0 ? null : index;
    }

    public void ClearFeatureCache()
    {
        lock (_featureIndices)
            _featureIndices.Clear();
    }

    /// <summary>Reads a HID++ 1.0 register of the receiver (short report, device index 0xFF).</summary>
    public async Task<byte[]> ReadReceiverRegisterAsync(byte register)
    {
        var reply = await RequestAsync(Build(ShortReportId, ReceiverIndex, 0x81, register, []), null).ConfigureAwait(false);
        return reply.Params[..3].ToArray();
    }

    /// <summary>Writes a 3-byte HID++ 1.0 register of the receiver.</summary>
    public Task WriteReceiverRegisterAsync(byte register, byte[] value)
        => RequestAsync(Build(ShortReportId, ReceiverIndex, 0x80, register, value), null);

    byte[] Build(byte reportId, byte deviceIndex, byte subId, byte address, byte[] args)
    {
        var supported = reportId == ShortReportId ? _transport.SupportsShort : _transport.SupportsLong;
        if (!supported)
            throw new NotSupportedException($"The device does not offer {(reportId == ShortReportId ? "short" : "long")} HID++ reports.");

        var length = reportId == ShortReportId ? ShortReportLength : LongReportLength;
        if (args.Length > length - 4)
            throw new ArgumentException($"Too many parameters ({args.Length}) for a {length}-byte report.", nameof(args));

        var report = new byte[length];
        report[0] = reportId;
        report[1] = deviceIndex;
        report[2] = subId;
        report[3] = address;
        args.CopyTo(report, 4);
        return report;
    }

    async Task<HidppReport> RequestAsync(byte[] report, TimeSpan? timeout)
    {
        await _requestLock.WaitAsync().ConfigureAwait(false);
        var pending = new PendingRequest(report[1], report[2], report[3]);
        try
        {
            lock (_pendingLock)
                _pending = pending;

            Traffic?.Invoke(new HidppReport(report), true);
            _transport.Send(report);

            try
            {
                return await pending.Completion.Task.WaitAsync(timeout ?? Timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"No reply from device 0x{report[1]:X2} (feature index 0x{report[2]:X2}, function {report[3] >> 4}).");
            }
        }
        finally
        {
            lock (_pendingLock)
                _pending = null;
            _requestLock.Release();
        }
    }

    void OnReport(byte[] data)
    {
        if (data.Length < 4 || (data[0] != ShortReportId && data[0] != LongReportId))
            return;

        var report = new HidppReport(data);
        Traffic?.Invoke(report, false);

        PendingRequest? pending;
        lock (_pendingLock)
            pending = _pending;

        if (pending is not null && pending.TryComplete(report))
            return;

        Notification?.Invoke(report);
    }

    sealed class PendingRequest(byte deviceIndex, byte subId, byte address)
    {
        public TaskCompletionSource<HidppReport> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryComplete(HidppReport report)
        {
            var d = report.Data;
            if (d[1] != deviceIndex)
                return false;

            if (d[2] == subId && d[3] == address)
            {
                Completion.TrySetResult(report);
                return true;
            }

            // Errors echo the request's sub ID / feature index and address after the error marker:
            // HID++ 1.0: [0x10, dev, 0x8F, subId, address, code, 0]
            // HID++ 2.0: [0x11, dev, 0xFF, featureIndex, address, code, ...]
            if (d.Length >= 6 && d[3] == subId && d[4] == address && d[2] is 0x8F or 0xFF)
            {
                var protocol = d[2] == 0x8F ? HidppProtocol.Hidpp10 : HidppProtocol.Hidpp20;
                Completion.TrySetException(new HidppException(protocol, d[5]));
                return true;
            }

            return false;
        }
    }
}
