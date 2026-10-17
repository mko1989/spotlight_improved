namespace Spotlight.Core.Hidpp;

public enum HidppProtocol
{
    /// <summary>Error reported by the receiver or a HID++ 1.0 device (sub ID 0x8F).</summary>
    Hidpp10,

    /// <summary>Error reported by a HID++ 2.0+ device (feature index 0xFF).</summary>
    Hidpp20,
}

public sealed class HidppException(HidppProtocol protocol, byte code)
    : Exception($"HID++ {(protocol == HidppProtocol.Hidpp10 ? "1.0" : "2.0")} error 0x{code:X2}: {Describe(protocol, code)}")
{
    public HidppProtocol Protocol { get; } = protocol;
    public byte Code { get; } = code;

    /// <summary>The receiver could not reach the device: it is asleep, out of range or switched off.</summary>
    public bool IsDeviceUnreachable => Protocol == HidppProtocol.Hidpp10 && Code is 0x04 or 0x08 or 0x09;

    static string Describe(HidppProtocol protocol, byte code) => protocol == HidppProtocol.Hidpp10
        ? code switch
        {
            0x01 => "invalid sub ID",
            0x02 => "invalid address",
            0x03 => "invalid value",
            0x04 => "connection failed",
            0x05 => "too many devices",
            0x06 => "already exists",
            0x07 => "busy",
            0x08 => "unknown device",
            0x09 => "resource error (device asleep or out of range?)",
            0x0A => "request unavailable",
            0x0B => "invalid parameter value",
            0x0C => "wrong PIN code",
            _ => "unknown error",
        }
        : code switch
        {
            0x01 => "unknown",
            0x02 => "invalid argument",
            0x03 => "out of range",
            0x04 => "hardware error",
            0x05 => "internal error",
            0x06 => "invalid feature index",
            0x07 => "invalid function",
            0x08 => "busy",
            0x09 => "unsupported",
            _ => "unknown error",
        };
}
