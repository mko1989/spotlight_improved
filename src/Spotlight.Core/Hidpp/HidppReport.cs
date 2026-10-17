namespace Spotlight.Core.Hidpp;

/// <summary>
/// A HID++ report as it travels on the wire:
/// <c>[reportId, deviceIndex, featureIndex, function &lt;&lt; 4 | softwareId, params...]</c>.
/// For HID++ 1.0 (receiver registers) byte 2 is the sub ID and byte 3 the register address.
/// </summary>
public readonly record struct HidppReport(byte[] Data)
{
    public byte ReportId => Data[0];
    public byte DeviceIndex => Data[1];
    public byte FeatureIndex => Data[2];
    public byte Address => Data[3];
    public int Function => Data[3] >> 4;
    public int SoftwareId => Data[3] & 0x0F;
    public ReadOnlySpan<byte> Params => Data.AsSpan(4);

    public override string ToString() => Hex.Format(Data);
}

public static class Hex
{
    public static string Format(ReadOnlySpan<byte> bytes) => BitConverter.ToString(bytes.ToArray()).Replace('-', ' ');
}
