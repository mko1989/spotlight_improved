using System.ComponentModel;
using System.Diagnostics;
using Spotlight.Core;
using Spotlight.Core.Hid;
using Spotlight.Core.Hidpp;

namespace SpotlightProbe;

/// <summary>An open connection to the Spotlight: transport, device index and feature table.</summary>
sealed class Session : IDisposable
{
    static readonly TimeSpan WakeTimeout = TimeSpan.FromSeconds(30);

    Session(HidTransport transport, HidppDevice device, ushort productId)
    {
        Transport = transport;
        Device = device;
        ProductId = productId;
    }

    public HidTransport Transport { get; }
    public HidppDevice Device { get; }
    public ushort ProductId { get; }
    public int ProtocolMajor { get; private set; }
    public int ProtocolMinor { get; private set; }
    public IReadOnlyList<FeatureEntry> Features { get; private set; } = [];

    public bool ViaReceiver => ProductId == SpotlightIds.ReceiverProductId;

    public string Description =>
        $"{SpotlightIds.DescribeProduct(ProductId)} ({SpotlightIds.LogitechVendorId:X4}:{ProductId:X4}), device index 0x{Device.DeviceIndex:X2}";

    public string FeatureName(byte index)
    {
        foreach (var feature in Features)
        {
            if (feature.Index == index)
                return $"0x{feature.Id:X4} {FeatureIds.Name(feature.Id)}";
        }
        return $"feature index 0x{index:X2}";
    }

    public static async Task<Session> ConnectAsync(Options options)
    {
        var found = SpotlightLocator.Find(options.ProductId) ?? throw new ProbeException(
            options.ProductId is { } pid
                ? $"No HID++ device with product ID {pid:X4} found. Run 'spotlight-probe list' to see what is connected."
                : "No Spotlight found (looked for the USB receiver 046D:C53E and Bluetooth 046D:B503). " +
                  "Run 'spotlight-probe list' to see what is connected.");

        HidTransport transport;
        try
        {
            transport = HidTransport.Open(found.Collections);
        }
        catch (Win32Exception e)
        {
            throw new ProbeException(
                $"{e.Message}. If Logi Options+ is running, quit it (also end logioptionsplus_agent.exe in Task Manager).");
        }

        try
        {
            transport.Failed += error => Log.Warn(error.Message);
            var device = new HidppDevice(transport, 0);
            if (options.Raw)
                device.Traffic += Log.Traffic;

            var session = new Session(transport, device, found.ProductId);
            await session.FindDeviceAsync(options.DeviceIndex);
            session.Features = await device.GetFeaturesAsync();
            return session;
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Pings candidate device indexes until one answers. The Spotlight sleeps after a few seconds without use and
    /// then cannot be reached through the receiver, so keep trying for a while and ask the user to wake it.
    /// </summary>
    async Task FindDeviceAsync(byte? forcedIndex)
    {
        byte[] candidates = forcedIndex is { } index ? [index]
            : ViaReceiver ? [1, 2, 3, 4, 5, 6]
            : [HidppDevice.ReceiverIndex];

        var clock = Stopwatch.StartNew();
        var asked = false;
        while (true)
        {
            Exception? last = null;
            foreach (var candidate in candidates)
            {
                Device.DeviceIndex = candidate;
                try
                {
                    (ProtocolMajor, ProtocolMinor) = await Device.PingAsync(TimeSpan.FromMilliseconds(800));
                    return;
                }
                catch (Exception e) when (e is HidppException or TimeoutException)
                {
                    last = e;
                }
            }

            if (clock.Elapsed > WakeTimeout)
                throw new ProbeException($"The Spotlight did not answer ({last?.Message}). Is it charged and paired with this receiver?");

            if (!asked)
            {
                Log.Info("Waiting for the Spotlight - press any button on it to wake it up...");
                asked = true;
            }
            await Task.Delay(500);
        }
    }

    public void Dispose() => Transport.Dispose();
}
