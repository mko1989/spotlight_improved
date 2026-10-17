using Spotlight.Core;
using Spotlight.Core.Hid;
using Spotlight.Core.Hidpp;

namespace SpotlightProbe;

static class Commands
{
    public static int List()
    {
        var collections = HidEnumerator.Enumerate(SpotlightIds.LogitechVendorId);
        if (collections.Count == 0)
        {
            Log.Plain("No Logitech HID devices found.");
            return 1;
        }

        Log.Plain("   VID:PID    usage       in  out  product");
        foreach (var c in collections.OrderBy(c => c.ProductId).ThenBy(c => c.UsagePage).ThenBy(c => c.Usage))
        {
            var mark = HidTransport.IsHidppCollection(c) ? "*" : " ";
            Log.Plain($"{mark}  {c.VendorId:X4}:{c.ProductId:X4}  {c.UsagePage:X4}:{c.Usage:X4}  {c.InputReportLength,3}  {c.OutputReportLength,3}  {c.ProductName}");
            Log.Plain($"       {c.Path}");
        }
        Log.Plain();
        Log.Plain("* = HID++ collection (vendor usage page, 7-byte short or 20-byte long reports)");
        return 0;
    }

    public static async Task<int> InfoAsync(Options options)
    {
        using var session = await Session.ConnectAsync(options);
        var device = session.Device;

        Log.Plain($"Device     {session.Description}");
        Log.Plain($"Protocol   HID++ {session.ProtocolMajor}.{session.ProtocolMinor}");
        Log.Plain($"Name       {await Query(device.GetNameAsync)}");
        Log.Plain($"Firmware   {await Query(async () => string.Join(", ", (await device.GetFirmwareAsync()).Select(f => $"{f.TypeName} {f.Version}")))}");
        Log.Plain($"Battery    {await Query(async () => await device.GetBatteryAsync() is { } b ? $"{b.Percent}% ({b.State})" : null)}");
        Log.Plain();
        Log.Plain("Features");
        Log.Plain("  idx  id      ver  name");
        foreach (var f in session.Features)
        {
            var tags = string.Join(' ', new[] { f.Obsolete ? "obsolete" : null, f.Hidden ? "hidden" : null, f.Engineering ? "engineering" : null }.OfType<string>());
            Log.Plain($"  {f.Index,3}  0x{f.Id:X4}  {f.Version,3}  {FeatureIds.Name(f.Id)} {tags}");
        }
        Log.Plain();
        Log.Plain("HID collections in use");
        foreach (var c in session.Transport.Collections)
            Log.Plain($"  {(HidTransport.IsShortCollection(c) ? "short" : "long "),-5}  {c.UsagePage:X4}:{c.Usage:X4}  {c.Path}");
        return 0;
    }

    public static async Task<int> ControlsAsync(Options options)
    {
        using var session = await Session.ConnectAsync(options);
        var reprog = await RequireReprogControlsAsync(session);
        var controls = await reprog.GetAllControlsAsync();

        Log.Plain($"{controls.Count} controls (ReprogControlsV4 at feature index 0x{reprog.FeatureIndex:X2})");
        Log.Plain();
        Log.Plain("  CID     TID     grp  name                         reporting       capabilities");
        foreach (var c in controls)
        {
            var reporting = await Query(async () =>
            {
                var r = await reprog.GetReportingAsync(c.Cid);
                var state = r.Flags == ReportingFlags.None ? "normal" : r.Flags.ToString();
                return r.RemappedTo != 0 && r.RemappedTo != c.Cid ? $"{state}, remapped to 0x{r.RemappedTo:X4}" : state;
            });
            Log.Plain($"  0x{c.Cid:X4}  0x{c.TaskId:X4}  {c.Group,3}  {SpotlightIds.ControlName(c.Cid),-28} {reporting,-15} {c.Flags}");
        }
        Log.Plain();
        Log.Plain("Divertable: the control can be reported to software instead of sending its normal key/click.");
        Log.Plain("RawXY:      motion while the control is held can be reported to software instead of moving the cursor.");
        return 0;
    }

    public static async Task<int> VibrateAsync(Options options)
    {
        using var session = await Session.ConnectAsync(options);
        if (await session.Device.GetFeatureIndexAsync(FeatureIds.PresenterControl) is not { } index)
            throw new ProbeException("This device has no PresenterControl feature (0x1A00).");

        // Same parameters Projecteur uses for the Spotlight: length (max 10), 0xE8, intensity.
        await session.Device.CallAsync(index, 1, (byte)Math.Max(1, options.VibrateLength), 0xE8, (byte)options.VibrateIntensity);
        Log.Good($"Vibration sent (length {options.VibrateLength}, intensity {options.VibrateIntensity}).");
        return 0;
    }

    public static async Task<int> RestoreAsync(Options options)
    {
        using var session = await Session.ConnectAsync(options);
        var reprog = await RequireReprogControlsAsync(session);
        const ReportingFlags anyDiversion = ReportingFlags.Diverted | ReportingFlags.PersistentlyDiverted
                                            | ReportingFlags.RawXYDiverted | ReportingFlags.ForceRawXYDiverted;

        var restored = 0;
        foreach (var control in await reprog.GetAllControlsAsync())
        {
            if (!control.Flags.HasFlag(ControlFlags.Divertable))
                continue;
            var reporting = await reprog.GetReportingAsync(control.Cid);
            if ((reporting.Flags & anyDiversion) == 0)
                continue;

            await reprog.SetReportingAsync(control.Cid, divert: false, rawXY: false, persist: false);
            Log.Good($"Restored 0x{control.Cid:X4} {SpotlightIds.ControlName(control.Cid)} (was {reporting.Flags})");
            restored++;
        }

        if (restored == 0)
            Log.Plain("Nothing was diverted - all buttons report normally.");
        return 0;
    }

    static async Task<ReprogControls> RequireReprogControlsAsync(Session session)
        => await ReprogControls.CreateAsync(session.Device)
           ?? throw new ProbeException("This device has no ReprogControlsV4 (0x1B04) feature, so its buttons cannot be diverted.");

    /// <summary>Runs an optional query; a failure becomes text in the output instead of aborting the command.</summary>
    static async Task<string> Query(Func<Task<string?>> query)
    {
        try
        {
            return await query() ?? "not supported";
        }
        catch (Exception e) when (e is HidppException or TimeoutException)
        {
            return $"n/a ({e.Message})";
        }
    }
}
