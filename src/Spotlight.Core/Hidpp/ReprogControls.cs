namespace Spotlight.Core.Hidpp;

/// <summary>Capability flags of a control (getCidInfo flags byte, plus the additional flags byte as bits 8+).</summary>
[Flags]
public enum ControlFlags : ushort
{
    None = 0,
    Mouse = 0x0001,
    Fn = 0x0002,
    NonStandard = 0x0004,
    FnSensitive = 0x0008,
    Reprogrammable = 0x0010,
    Divertable = 0x0020,
    PersistentlyDivertable = 0x0040,
    Virtual = 0x0080,
    RawXY = 0x0100,
    ForceRawXY = 0x0200,
    AnalyticsKeyEvents = 0x0400,
}

/// <summary>Current reporting state of a control (getCidReporting).</summary>
[Flags]
public enum ReportingFlags : byte
{
    None = 0,
    Diverted = 0x01,
    PersistentlyDiverted = 0x04,
    RawXYDiverted = 0x10,
    ForceRawXYDiverted = 0x40,
}

public readonly record struct ControlInfo(ushort Cid, ushort TaskId, ControlFlags Flags, byte Position, byte Group, byte GroupMask);

public readonly record struct ControlReporting(ushort Cid, ReportingFlags Flags, ushort RemappedTo);

/// <summary>
/// Feature 0x1B04 (REPROG_CONTROLS_V4). Diverting a control makes the device report it to software over HID++
/// instead of sending its normal HID keystroke/click; with raw XY also diverted, the motion produced while the
/// control is held arrives as HID++ events instead of moving the system cursor.
/// </summary>
public sealed class ReprogControls
{
    const byte FnGetCount = 0, FnGetCidInfo = 1, FnGetCidReporting = 2, FnSetCidReporting = 3;
    const byte EventDivertedButtons = 0, EventRawXY = 1;

    readonly HidppDevice _device;

    ReprogControls(HidppDevice device, byte featureIndex)
    {
        _device = device;
        FeatureIndex = featureIndex;
    }

    public byte FeatureIndex { get; }

    public static async Task<ReprogControls?> CreateAsync(HidppDevice device)
        => await device.GetFeatureIndexAsync(FeatureIds.ReprogControlsV4).ConfigureAwait(false) is { } index
            ? new ReprogControls(device, index)
            : null;

    public async Task<int> GetCountAsync()
        => (await _device.CallAsync(FeatureIndex, FnGetCount).ConfigureAwait(false)).Params[0];

    public async Task<ControlInfo> GetControlInfoAsync(int index)
        => ParseControlInfo(await _device.CallAsync(FeatureIndex, FnGetCidInfo, (byte)index).ConfigureAwait(false));

    public async Task<IReadOnlyList<ControlInfo>> GetAllControlsAsync()
    {
        var count = await GetCountAsync().ConfigureAwait(false);
        var controls = new List<ControlInfo>(count);
        for (var i = 0; i < count; i++)
            controls.Add(await GetControlInfoAsync(i).ConfigureAwait(false));
        return controls;
    }

    public async Task<ControlReporting> GetReportingAsync(ushort cid)
        => ParseReporting(await _device.CallAsync(FeatureIndex, FnGetCidReporting, Hi(cid), Lo(cid)).ConfigureAwait(false));

    /// <summary>
    /// Sets how a control reports. Each setting is paired with a "valid" bit so the device only changes what
    /// we ask for; <paramref name="persist"/> is left untouched when <c>null</c>. A non-persistent diversion is
    /// dropped by the device when it resets.
    /// </summary>
    public Task SetReportingAsync(ushort cid, bool divert, bool rawXY, bool? persist = null)
        => _device.CallAsync(FeatureIndex, FnSetCidReporting, Hi(cid), Lo(cid), BuildReportingFlags(divert, rawXY, persist), 0, 0);

    internal static byte BuildReportingFlags(bool divert, bool rawXY, bool? persist)
    {
        const byte divertValid = 0x02, persistValid = 0x08, rawXYValid = 0x20;
        var flags = (byte)(divertValid | rawXYValid);
        if (divert)
            flags |= 0x01;
        if (rawXY)
            flags |= 0x10;
        if (persist is { } keep)
            flags |= (byte)(persistValid | (keep ? 0x04 : 0));
        return flags;
    }

    /// <summary>divertedButtonsEvent: the diverted controls currently held (up to four). Empty when all are released.</summary>
    public bool TryParseDivertedButtons(HidppReport report, out ushort[] pressed)
    {
        pressed = [];
        if (!IsEvent(report, EventDivertedButtons))
            return false;

        var p = report.Params;
        var cids = new List<ushort>(4);
        for (var i = 0; i + 1 < Math.Min(p.Length, 8); i += 2)
        {
            var cid = (ushort)(p[i] << 8 | p[i + 1]);
            if (cid != 0)
                cids.Add(cid);
        }
        pressed = [.. cids];
        return true;
    }

    /// <summary>divertedRawXYEvent: signed 16-bit big-endian motion deltas.</summary>
    public bool TryParseRawXY(HidppReport report, out short dx, out short dy)
    {
        dx = dy = 0;
        if (!IsEvent(report, EventRawXY) || report.Params.Length < 4)
            return false;

        var p = report.Params;
        dx = (short)(p[0] << 8 | p[1]);
        dy = (short)(p[2] << 8 | p[3]);
        return true;
    }

    bool IsEvent(HidppReport report, int function)
        => report.DeviceIndex == _device.DeviceIndex && report.FeatureIndex == FeatureIndex
           && report.Function == function && report.SoftwareId == 0;

    internal static ControlInfo ParseControlInfo(HidppReport reply)
    {
        var p = reply.Params;
        var additionalFlags = p.Length > 8 ? p[8] : 0;
        return new ControlInfo(
            Cid: (ushort)(p[0] << 8 | p[1]),
            TaskId: (ushort)(p[2] << 8 | p[3]),
            Flags: (ControlFlags)(p[4] | additionalFlags << 8),
            Position: p[5],
            Group: p[6],
            GroupMask: p[7]);
    }

    internal static ControlReporting ParseReporting(HidppReport reply)
    {
        var p = reply.Params;
        return new ControlReporting((ushort)(p[0] << 8 | p[1]), (ReportingFlags)p[2], (ushort)(p.Length > 4 ? p[3] << 8 | p[4] : 0));
    }

    static byte Hi(ushort value) => (byte)(value >> 8);
    static byte Lo(ushort value) => (byte)value;
}
