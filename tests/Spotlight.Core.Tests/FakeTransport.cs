using Spotlight.Core.Hidpp;

namespace Spotlight.Core.Tests;

/// <summary>In-memory transport: records what is sent and lets a test answer like a device would.</summary>
sealed class FakeTransport : IHidppTransport
{
    public List<byte[]> Sent { get; } = [];
    public Func<byte[], byte[]?>? Responder { get; set; }
    public bool SupportsShort { get; set; } = true;
    public bool SupportsLong { get; set; } = true;

    public event Action<byte[]>? ReportReceived;
    public event Action<Exception>? Failed;

    public bool Disposed { get; private set; }

    public void Fail() => Failed?.Invoke(new IOException("unplugged"));

    public void Send(byte[] report)
    {
        Sent.Add(report);
        if (Responder?.Invoke(report) is { } reply)
            ReportReceived?.Invoke(reply);
    }

    public void Inject(params byte[] report) => ReportReceived?.Invoke(report);

    public void Dispose() => Disposed = true;

    /// <summary>A long reply echoing the request header, with the given parameters.</summary>
    public static byte[] Reply(byte[] request, params byte[] parameters)
    {
        var reply = new byte[HidppDevice.LongReportLength];
        reply[0] = HidppDevice.LongReportId;
        Array.Copy(request, 1, reply, 1, 3);
        parameters.CopyTo(reply, 4);
        return reply;
    }
}
