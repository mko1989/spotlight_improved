using System.Text;

namespace Spotlight.Core.Hidpp;

public readonly record struct FeatureEntry(byte Index, ushort Id, byte Type, byte Version)
{
    public bool Obsolete => (Type & 0x80) != 0;
    public bool Hidden => (Type & 0x40) != 0;
    public bool Engineering => (Type & 0x20) != 0;
}

public readonly record struct FirmwareEntity(int Type, string Version)
{
    public string TypeName => Type switch { 0 => "main", 1 => "bootloader", 2 => "hardware", _ => $"type {Type}" };
}

public readonly record struct BatteryInfo(int Percent, string State);

/// <summary>Read-only queries of common HID++ 2.0 features.</summary>
public static class DeviceQueries
{
    /// <summary>All features (index 0 = Root, then FeatureSet entries 1..count).</summary>
    public static async Task<IReadOnlyList<FeatureEntry>> GetFeaturesAsync(this HidppDevice device)
    {
        var features = new List<FeatureEntry> { new(0, FeatureIds.Root, 0, 0) };
        if (await device.GetFeatureIndexAsync(FeatureIds.FeatureSet) is not { } setIndex)
            return features;

        int count = (await device.CallAsync(setIndex, 0)).Params[0];
        for (var i = 1; i <= count; i++)
        {
            var reply = await device.CallAsync(setIndex, 1, (byte)i);
            var p = reply.Params;
            features.Add(new FeatureEntry((byte)i, (ushort)(p[0] << 8 | p[1]), p[2], p[3]));
        }
        return features;
    }

    public static async Task<string?> GetNameAsync(this HidppDevice device)
    {
        if (await device.GetFeatureIndexAsync(FeatureIds.DeviceName) is not { } index)
            return null;

        int length = (await device.CallAsync(index, 0)).Params[0];
        var name = new StringBuilder();
        while (name.Length < length)
        {
            var reply = await device.CallAsync(index, 1, (byte)name.Length);
            var chunk = reply.Params[..Math.Min(reply.Params.Length, length - name.Length)];
            if (chunk.IsEmpty)
                break;
            name.Append(Encoding.UTF8.GetString(chunk));
        }
        return name.ToString().TrimEnd('\0');
    }

    public static async Task<IReadOnlyList<FirmwareEntity>> GetFirmwareAsync(this HidppDevice device)
    {
        if (await device.GetFeatureIndexAsync(FeatureIds.FirmwareVersion) is not { } index)
            return [];

        int count = (await device.CallAsync(index, 0)).Params[0];
        var entities = new List<FirmwareEntity>();
        for (var i = 0; i < count; i++)
        {
            var reply = await device.CallAsync(index, 1, (byte)i);
            var p = reply.Params;
            var prefix = Encoding.ASCII.GetString(p[1..4]).TrimEnd('\0', ' ');
            var build = p[6] << 8 | p[7];
            entities.Add(new FirmwareEntity(p[0], $"{prefix}{p[4]:X2}.{p[5]:X2}_B{build:X4}"));
        }
        return entities;
    }

    public static async Task<BatteryInfo?> GetBatteryAsync(this HidppDevice device)
    {
        if (await device.GetFeatureIndexAsync(FeatureIds.BatteryStatus) is { } legacy)
        {
            var p = (await device.CallAsync(legacy, 0)).Params;
            var state = p[2] switch
            {
                0 => "discharging", 1 => "recharging", 2 => "almost full", 3 => "charged",
                4 => "slow recharge", 5 => "invalid battery", 6 => "thermal error", _ => $"status {p[2]}",
            };
            return new BatteryInfo(p[0], state);
        }

        if (await device.GetFeatureIndexAsync(FeatureIds.UnifiedBattery) is { } unified)
        {
            var p = (await device.CallAsync(unified, 1)).Params;
            var state = p[2] switch
            {
                0 => "discharging", 1 => "charging", 2 => "charging (slow)", 3 => "charged", 4 => "charging error",
                _ => $"status {p[2]}",
            };
            return new BatteryInfo(p[0], state);
        }

        return null;
    }
}
