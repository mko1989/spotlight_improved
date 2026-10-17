using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using static Spotlight.Core.Hid.NativeMethods;

namespace Spotlight.Core.Hid;

/// <summary>
/// Read/write access to one HID collection. Input reports are read on a background thread.
/// </summary>
/// <remarks>
/// Uses two synchronous handles: synchronous I/O on a single handle is serialized by Windows, so a blocking
/// ReadFile would otherwise stall every WriteFile until the next input report arrives.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HidChannel : IDisposable
{
    readonly SafeFileHandle _readHandle;
    readonly SafeFileHandle _writeHandle;
    readonly Thread _readThread;
    volatile bool _disposed;

    HidChannel(HidDeviceInfo info, SafeFileHandle readHandle, SafeFileHandle writeHandle)
    {
        Info = info;
        _readHandle = readHandle;
        _writeHandle = writeHandle;
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = $"HID read {info.UsagePage:X4}:{info.Usage:X4}" };
    }

    public HidDeviceInfo Info { get; }

    /// <summary>Raised on the read thread for every input report (report ID included).</summary>
    public event Action<HidChannel, byte[]>? ReportReceived;

    /// <summary>Raised on the read thread when reading stops unexpectedly, e.g. the device was unplugged.</summary>
    public event Action<HidChannel, Win32Exception>? ReadFailed;

    public static HidChannel Open(HidDeviceInfo info)
    {
        var readHandle = OpenHandle(info.Path);
        SafeFileHandle writeHandle;
        try
        {
            writeHandle = OpenHandle(info.Path);
        }
        catch
        {
            readHandle.Dispose();
            throw;
        }

        var channel = new HidChannel(info, readHandle, writeHandle);
        channel._readThread.Start();
        return channel;
    }

    static SafeFileHandle OpenHandle(string path)
    {
        var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Cannot open HID device ({new Win32Exception(error).Message})");
        }
        return handle;
    }

    /// <summary>Writes an output report, zero-padded to the collection's report length.</summary>
    public void Write(ReadOnlySpan<byte> report)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var buffer = new byte[Math.Max(Info.OutputReportLength, report.Length)];
        report.CopyTo(buffer);
        if (!WriteFile(_writeHandle, buffer, buffer.Length, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException($"HID write failed ({new Win32Exception(error).Message})", new Win32Exception(error));
        }
    }

    void ReadLoop()
    {
        var buffer = new byte[Math.Max(Info.InputReportLength, 64)];
        while (!_disposed)
        {
            if (!ReadFile(_readHandle, buffer, buffer.Length, out var read, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (!_disposed && error != ERROR_OPERATION_ABORTED)
                    ReadFailed?.Invoke(this, new Win32Exception(error));
                return;
            }

            if (read > 0)
                ReportReceived?.Invoke(this, buffer.AsSpan(0, read).ToArray());
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CancelIoEx(_readHandle, IntPtr.Zero);
        _readThread.Join(TimeSpan.FromMilliseconds(500));
        _readHandle.Dispose();
        _writeHandle.Dispose();
    }
}
