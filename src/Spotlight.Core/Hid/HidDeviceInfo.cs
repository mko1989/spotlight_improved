namespace Spotlight.Core.Hid;

/// <summary>
/// One HID top-level collection. Windows exposes every top-level collection of a USB interface as its own
/// device path, so a single receiver shows up as several of these (keyboard, mouse, HID++ short, HID++ long, ...).
/// Report lengths include the leading report ID byte.
/// </summary>
public sealed record HidDeviceInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    int InputReportLength,
    int OutputReportLength,
    string? ProductName)
{
    public bool IsVendorDefined => UsagePage >= 0xFF00;
}
