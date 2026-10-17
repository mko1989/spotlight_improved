using System.Runtime.Versioning;

namespace Spotlight.Core.Hid;

[SupportedOSPlatform("windows")]
public static class SpotlightLocator
{
    static readonly ushort[] DefaultProductIds = [SpotlightIds.ReceiverProductId, SpotlightIds.BluetoothProductId];

    /// <summary>
    /// Finds the HID++ collections of the first present Spotlight (receiver preferred over Bluetooth),
    /// or of <paramref name="productId"/> when given. Returns <c>null</c> when none is connected.
    /// </summary>
    public static (ushort ProductId, IReadOnlyList<HidDeviceInfo> Collections)? Find(ushort? productId = null)
    {
        var logitech = HidEnumerator.Enumerate(SpotlightIds.LogitechVendorId);
        ushort[] candidates = productId is { } pid ? [pid] : DefaultProductIds;
        foreach (var candidate in candidates)
        {
            var hidpp = logitech.Where(c => c.ProductId == candidate && HidTransport.IsHidppCollection(c)).ToList();
            if (hidpp.Count > 0)
                return (candidate, hidpp);
        }
        return null;
    }
}
