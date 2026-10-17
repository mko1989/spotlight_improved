namespace Spotlight.Core;

public static class SpotlightIds
{
    public const ushort LogitechVendorId = 0x046D;

    /// <summary>Spotlight's own USB receiver.</summary>
    public const ushort ReceiverProductId = 0xC53E;

    /// <summary>Spotlight connected directly over Bluetooth LE.</summary>
    public const ushort BluetoothProductId = 0xB503;

    // Control IDs (ReprogControlsV4) as a Spotlight reports them (Solaar issue #2881). All are divertable; only
    // D8, DA and DC can also divert raw XY. The group masks are empty, so controls cannot be remapped onto each other.
    public const ushort LeftButton = 0x0050;            // quick press of the top button: left click
    public const ushort CursorButtonLongPress = 0x00D8; // top button held: moves the cursor
    public const ushort NextShortPress = 0x00D9;        // Right arrow
    public const ushort NextLongPress = 0x00DA;         // "Fast Forward" - does nothing useful by default
    public const ushort BackShortPress = 0x00DB;        // Left arrow
    public const ushort BackLongPress = 0x00DC;         // "Fast Backward" - does nothing useful by default
    public const ushort SwitchHighlight = 0x00DF;

    static readonly Dictionary<ushort, string> ControlNames = new()
    {
        [LeftButton] = "Top button, quick press (click)",
        [CursorButtonLongPress] = "Top button, held (pointer)",
        [NextShortPress] = "Next, short press",
        [NextLongPress] = "Next, long press",
        [BackShortPress] = "Back, short press",
        [BackLongPress] = "Back, long press",
        [SwitchHighlight] = "Switch highlight",
    };

    public static string ControlName(ushort cid) => ControlNames.GetValueOrDefault(cid, "unknown");

    public static string DescribeProduct(ushort productId) => productId switch
    {
        ReceiverProductId => "Spotlight USB receiver",
        BluetoothProductId => "Spotlight (Bluetooth)",
        _ => $"Logitech device {productId:X4}",
    };
}
