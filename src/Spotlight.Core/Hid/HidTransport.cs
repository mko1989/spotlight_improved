using System.Runtime.Versioning;
using Spotlight.Core.Hidpp;

namespace Spotlight.Core.Hid;

/// <summary>
/// HID++ over Windows HID. Windows splits the vendor interface into one collection per report size, so short
/// (0x10, 7 bytes) and long (0x11, 20 bytes) reports live on separate device paths; replies can arrive on either.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HidTransport : IHidppTransport
{
    readonly HidChannel? _short;
    readonly HidChannel? _long;

    HidTransport(HidChannel? shortChannel, HidChannel? longChannel)
    {
        _short = shortChannel;
        _long = longChannel;
        foreach (var channel in new[] { _short, _long })
        {
            if (channel is null)
                continue;
            channel.ReportReceived += (_, report) => ReportReceived?.Invoke(report);
            channel.ReadFailed += (ch, error) => Failed?.Invoke(
                new IOException($"Lost HID collection {ch.Info.UsagePage:X4}:{ch.Info.Usage:X4}: {error.Message}", error));
        }
    }

    public bool SupportsShort => _short is not null;
    public bool SupportsLong => _long is not null;
    public IEnumerable<HidDeviceInfo> Collections => new[] { _short?.Info, _long?.Info }.OfType<HidDeviceInfo>();

    public event Action<byte[]>? ReportReceived;

    /// <summary>Reading from a collection stopped, typically because the receiver was unplugged.</summary>
    public event Action<Exception>? Failed;

    public static bool IsShortCollection(HidDeviceInfo c) => c.IsVendorDefined && c.OutputReportLength == HidppDevice.ShortReportLength;
    public static bool IsLongCollection(HidDeviceInfo c) => c.IsVendorDefined && c.OutputReportLength == HidppDevice.LongReportLength;
    public static bool IsHidppCollection(HidDeviceInfo c) => IsShortCollection(c) || IsLongCollection(c);

    public static HidTransport Open(IEnumerable<HidDeviceInfo> collections)
    {
        var list = collections.ToList();
        var shortInfo = list.FirstOrDefault(IsShortCollection);
        var longInfo = list.FirstOrDefault(IsLongCollection);
        if (shortInfo is null && longInfo is null)
            throw new InvalidOperationException("No HID++ collection among the given HID collections.");

        HidChannel? shortChannel = null;
        try
        {
            shortChannel = shortInfo is null ? null : HidChannel.Open(shortInfo);
            var longChannel = longInfo is null ? null : HidChannel.Open(longInfo);
            return new HidTransport(shortChannel, longChannel);
        }
        catch
        {
            shortChannel?.Dispose();
            throw;
        }
    }

    public void Send(byte[] report)
    {
        var channel = report[0] == HidppDevice.ShortReportId ? _short : _long;
        if (channel is null)
            throw new NotSupportedException($"No HID collection for report ID 0x{report[0]:X2}.");
        channel.Write(report);
    }

    public void Dispose()
    {
        _short?.Dispose();
        _long?.Dispose();
    }
}
