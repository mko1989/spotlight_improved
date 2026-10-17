using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using static Spotlight.Core.Hid.NativeMethods;

namespace Spotlight.Core.Hid;

[SupportedOSPlatform("windows")]
public static class HidEnumerator
{
    /// <summary>Lists present HID collections, optionally only those of one vendor.</summary>
    public static IReadOnlyList<HidDeviceInfo> Enumerate(ushort? vendorId = null)
    {
        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE_VALUE)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed");

        var result = new List<HidDeviceInfo>();
        try
        {
            for (uint i = 0; ; i++)
            {
                var interfaceData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref interfaceData))
                    break; // ERROR_NO_MORE_ITEMS

                var path = GetDevicePath(set, ref interfaceData);
                if (path is null)
                    continue;

                var info = Query(path);
                if (info is null || (vendorId is { } vid && info.VendorId != vid))
                    continue;

                result.Add(info);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    static string? GetDevicePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA interfaceData)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, IntPtr.Zero, 0, out var size, IntPtr.Zero);
        if (size == 0)
            return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W: DWORD cbSize followed by the path. cbSize is the size of the
            // fixed part including struct padding, which differs between 64-bit and 32-bit processes.
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, buffer, size, out _, IntPtr.Zero))
                return null;
            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Reads attributes and capabilities without requesting read/write access, which also works for
    /// collections Windows holds exclusively (keyboards, mice).</summary>
    static HidDeviceInfo? Query(string path)
    {
        using var handle = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;

        var attributes = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
        if (!HidD_GetAttributes(handle, ref attributes))
            return null;

        ushort usagePage = 0, usage = 0;
        int inputLength = 0, outputLength = 0;
        if (HidD_GetPreparsedData(handle, out var preparsed))
        {
            try
            {
                if (HidP_GetCaps(preparsed, out var caps) == HIDP_STATUS_SUCCESS)
                {
                    usagePage = caps.UsagePage;
                    usage = caps.Usage;
                    inputLength = caps.InputReportByteLength;
                    outputLength = caps.OutputReportByteLength;
                }
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }

        return new HidDeviceInfo(path, attributes.VendorID, attributes.ProductID, usagePage, usage,
            inputLength, outputLength, GetProductString(handle));
    }

    static string? GetProductString(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var buffer = new byte[256];
        if (!HidD_GetProductString(handle, buffer, buffer.Length))
            return null;
        var text = Encoding.Unicode.GetString(buffer);
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }
}
