using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

namespace SpotlightProbe;

static class Program
{
    const string Usage = """
        spotlight-probe - talk to a Logitech Spotlight over HID++ (Windows)

        Usage: spotlight-probe <command> [options]

        Commands
          list        List Logitech HID collections (HID++ ones are marked with *)
          info        Protocol version, name, firmware, battery and feature table
          controls    Buttons exposed through ReprogControlsV4 and what they support
          monitor     Divert buttons to this tool and print presses, releases and raw motion
          vibrate     Make the Spotlight vibrate
          restore     Undo every button diversion (use if a button stopped working)

        Options
          --pid <hex>          Product ID (default: C53E receiver, then B503 Bluetooth)
          --index <n>          HID++ device index (default: auto)
          --raw                Print every HID++ report sent and received

        monitor
          --cid <hex,...|all>  Controls to divert (default: D8 = top button held)
          --no-rawxy           Divert buttons only, leave motion going to Windows

        vibrate
          --length <1-10>      Duration (default 3)
          --intensity <0-255>  Strength (default 128)

        Quit Logi Options+ first - it fights over the same settings.
        """;

    static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("spotlight-probe uses the Windows HID stack and only runs on Windows.");
            return 1;
        }

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            return options.Command switch
            {
                "list" => Commands.List(),
                "info" => await Commands.InfoAsync(options),
                "controls" => await Commands.ControlsAsync(options),
                "monitor" => await new Monitor(options).RunAsync(),
                "vibrate" => await Commands.VibrateAsync(options),
                "restore" => await Commands.RestoreAsync(options),
                _ => PrintUsage(),
            };
        }
        catch (ProbeException e)
        {
            Log.Error(e.Message);
            return 1;
        }
    }

    static int PrintUsage()
    {
        Console.WriteLine(Usage);
        return 0;
    }
}

/// <summary>An expected failure with a message meant for the user.</summary>
sealed class ProbeException(string message) : Exception(message);
