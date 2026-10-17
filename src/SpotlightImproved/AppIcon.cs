using System.Drawing.Drawing2D;

namespace SpotlightImproved;

/// <summary>Tray/window icon drawn at startup: a red laser dot on a dark disc.</summary>
static class AppIcon
{
    public static Icon Instance { get; } = Create();

    static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var disc = new SolidBrush(Color.FromArgb(40, 40, 48));
            g.FillEllipse(disc, 1, 1, 30, 30);
            using var glow = new SolidBrush(Color.FromArgb(110, 255, 40, 40));
            g.FillEllipse(glow, 7, 7, 18, 18);
            using var dot = new SolidBrush(Color.FromArgb(255, 60, 60));
            g.FillEllipse(dot, 11, 11, 10, 10);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
