namespace Spotlight.Core.Hidpp;

/// <summary>HID++ 2.0 feature IDs relevant to the Spotlight.</summary>
public static class FeatureIds
{
    public const ushort Root = 0x0000;
    public const ushort FeatureSet = 0x0001;
    public const ushort FirmwareVersion = 0x0003;
    public const ushort DeviceName = 0x0005;
    public const ushort BatteryStatus = 0x1000;
    public const ushort UnifiedBattery = 0x1004;
    public const ushort Haptic = 0x19B0;
    public const ushort PresenterControl = 0x1A00;
    public const ushort Sensor3D = 0x1A01;
    public const ushort ReprogControlsV4 = 0x1B04;
    public const ushort WirelessDeviceStatus = 0x1D4B;
    public const ushort PointerSpeed = 0x2205;

    static readonly Dictionary<ushort, string> Names = new()
    {
        [Root] = "Root",
        [FeatureSet] = "FeatureSet",
        [0x0002] = "FeatureInfo",
        [FirmwareVersion] = "FirmwareVersion",
        [DeviceName] = "DeviceName",
        [0x0007] = "DeviceFriendlyName",
        [0x0020] = "ConfigChange",
        [0x00C2] = "DfuControlSigned",
        [0x00D0] = "Dfu",
        [BatteryStatus] = "BatteryStatus",
        [UnifiedBattery] = "UnifiedBattery",
        [0x1814] = "ChangeHost",
        [0x1815] = "HostsInfo",
        [Haptic] = "Haptic",
        [PresenterControl] = "PresenterControl",
        [Sensor3D] = "Sensor3D",
        [ReprogControlsV4] = "ReprogControlsV4",
        [WirelessDeviceStatus] = "WirelessDeviceStatus",
        [0x2005] = "SwapButtonCancel",
        [PointerSpeed] = "PointerSpeed",
    };

    public static string Name(ushort featureId) => Names.GetValueOrDefault(featureId, "?");
}
