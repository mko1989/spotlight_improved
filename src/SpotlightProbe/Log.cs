using System.Diagnostics;
using Spotlight.Core.Hidpp;

namespace SpotlightProbe;

/// <summary>Thread-safe console output; HID notifications are printed from reader threads.</summary>
static class Log
{
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly Lock Gate = new();

    public static long ElapsedMs => Clock.ElapsedMilliseconds;

    public static void Plain(string text = "") => Write(null, text, stamp: false);
    public static void Info(string text) => Write(null, text);
    public static void Event(string text) => Write(ConsoleColor.Cyan, text);
    public static void Detail(string text) => Write(ConsoleColor.DarkGray, text);
    public static void Good(string text) => Write(ConsoleColor.Green, text);
    public static void Warn(string text) => Write(ConsoleColor.Yellow, "warning: " + text);
    public static void Error(string text) => Write(ConsoleColor.Red, "error: " + text);

    public static void Traffic(HidppReport report, bool sent)
        => Write(ConsoleColor.DarkGray, $"{(sent ? "->" : "<-")} {report}");

    static void Write(ConsoleColor? color, string text, bool stamp = true)
    {
        lock (Gate)
        {
            if (color is { } c)
                Console.ForegroundColor = c;
            Console.WriteLine(stamp ? $"[{Clock.Elapsed.TotalSeconds,8:0.000}] {text}" : text);
            if (color is not null)
                Console.ResetColor();
        }
    }
}
