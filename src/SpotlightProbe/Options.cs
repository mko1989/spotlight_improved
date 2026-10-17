using System.Globalization;

namespace SpotlightProbe;

sealed class Options
{
    public string Command { get; private set; } = "help";
    public ushort? ProductId { get; private set; }
    public byte? DeviceIndex { get; private set; }
    public bool Raw { get; private set; }
    public List<ushort> Cids { get; } = [];
    public bool AllCids { get; private set; }
    public bool NoRawXY { get; private set; }
    public int VibrateLength { get; private set; } = 3;
    public int VibrateIntensity { get; private set; } = 128;

    public static Options Parse(string[] args)
    {
        var options = new Options();
        var i = 0;
        if (args.Length > 0 && !args[0].StartsWith('-'))
            options.Command = args[i++].ToLowerInvariant();

        for (; i < args.Length; i++)
        {
            var arg = args[i];
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{arg} needs a value.");

            switch (arg)
            {
                case "--pid":
                    options.ProductId = (ushort)ParseHex(Value(), arg, ushort.MaxValue);
                    break;
                case "--index":
                    options.DeviceIndex = (byte)ParseNumber(Value(), arg, byte.MaxValue);
                    break;
                case "--raw":
                    options.Raw = true;
                    break;
                case "--cid":
                    var cids = Value();
                    if (cids.Equals("all", StringComparison.OrdinalIgnoreCase))
                        options.AllCids = true;
                    else
                        options.Cids.AddRange(cids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(c => (ushort)ParseHex(c, arg, ushort.MaxValue)));
                    break;
                case "--no-rawxy":
                    options.NoRawXY = true;
                    break;
                case "--length":
                    options.VibrateLength = ParseNumber(Value(), arg, 10);
                    break;
                case "--intensity":
                    options.VibrateIntensity = ParseNumber(Value(), arg, 255);
                    break;
                case "-h" or "--help":
                    options.Command = "help";
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{arg}'.");
            }
        }
        return options;
    }

    /// <summary>Hex with or without 0x prefix ("D8", "0xD8").</summary>
    static int ParseHex(string text, string option, int max)
    {
        var digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (!int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) || value < 0 || value > max)
            throw new ArgumentException($"{option}: '{text}' is not a hex number up to 0x{max:X}.");
        return value;
    }

    /// <summary>Decimal, or hex with 0x prefix.</summary>
    static int ParseNumber(string text, string option, int max)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ParseHex(text, option, max);
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0 || value > max)
            throw new ArgumentException($"{option}: '{text}' is not a number between 0 and {max}.");
        return value;
    }
}
