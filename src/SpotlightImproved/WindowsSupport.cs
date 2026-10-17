using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using Spotlight.Core;
using Spotlight.Core.Hid;

namespace SpotlightImproved;

static class WindowsConnector
{
    /// <summary>Opens the Spotlight's HID++ collections, or returns null when no Spotlight is plugged in.</summary>
    public static SpotlightConnection? TryConnect()
    {
        if (SpotlightLocator.Find() is not { } found)
            return null;
        try
        {
            return new SpotlightConnection(HidTransport.Open(found.Collections), found.ProductId);
        }
        catch (Win32Exception e)
        {
            throw new IOException($"Cannot open the Spotlight receiver: {e.Message}", e);
        }
    }
}

static class LogiOptions
{
    static readonly string[] ProcessNames = ["logioptionsplus_agent", "logioptionsplus"];

    public static bool IsRunning() => ProcessNames.Any(name =>
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes)
            p.Dispose();
        return processes.Length > 0;
    });
}

static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "SpotlightImproved";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
