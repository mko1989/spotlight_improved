namespace Spotlight.Core.Hidpp;

/// <summary>Moves raw HID++ reports (report ID first) to and from a device.</summary>
public interface IHidppTransport : IDisposable
{
    /// <summary>Short reports: ID 0x10, 7 bytes.</summary>
    bool SupportsShort { get; }

    /// <summary>Long reports: ID 0x11, 20 bytes.</summary>
    bool SupportsLong { get; }

    void Send(byte[] report);

    /// <summary>Raised for every HID++ report received; may be raised concurrently from several threads.</summary>
    event Action<byte[]>? ReportReceived;

    /// <summary>The connection broke (e.g. receiver unplugged); no further reports will arrive.</summary>
    event Action<Exception>? Failed;
}
