using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SpotlightImproved.Overlay;

/// <summary>
/// Pre-rendered pointer pixels (premultiplied BGRA). <see cref="Background"/> is what the rest of the screen
/// shows: transparent for a dot or ring, the dimming color for the spotlight. The sprite's edges blend into it.
/// </summary>
sealed class Sprite(int width, int height, uint[] pixels, uint background)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public uint[] Pixels { get; } = pixels;
    public uint Background { get; } = background;
}

/// <summary>Everything that decides how the pointer looks, already scaled to the target screen.</summary>
readonly record struct PointerLook(
    PointerStyle Style,
    Color Color,
    double Diameter,
    double Opacity,
    double Glow,
    double SpotlightRadius,
    double SpotlightDim)
{
    public static PointerLook From(AppSettings s, double scale) => new(
        s.Style, s.Color, s.Size * scale, s.Opacity / 100.0, s.Glow / 100.0, s.SpotlightRadius * scale, s.SpotlightDim / 100.0);
}

static class SpriteRenderer
{
    public static Sprite Render(PointerLook look) => look.Style switch
    {
        PointerStyle.Ring => RenderRing(look),
        PointerStyle.Spotlight => RenderSpotlight(look),
        _ => RenderDot(look),
    };

    static Sprite RenderDot(PointerLook look)
    {
        var radius = Math.Max(2, look.Diameter / 2);
        var glowRadius = radius * (1 + 1.6 * look.Glow);
        var size = (int)Math.Ceiling(glowRadius * 2) + 4;
        return Draw(size, size, g =>
        {
            var c = size / 2f;
            if (look.Glow > 0)
                FillSoftCircle(g, c, (float)glowRadius, Color.FromArgb(Alpha(0.75 * look.Glow * look.Opacity), look.Color));

            using (var core = new SolidBrush(Color.FromArgb(Alpha(look.Opacity), look.Color)))
                g.FillEllipse(core, c - (float)radius, c - (float)radius, (float)radius * 2, (float)radius * 2);

            // Bright center, like a real laser dot.
            FillSoftCircle(g, c, (float)(radius * 0.7), Color.FromArgb(Alpha(0.85 * look.Opacity), Mix(look.Color, Color.White, 0.65)));
        });
    }

    static Sprite RenderRing(PointerLook look)
    {
        var diameter = Math.Max(6, look.Diameter);
        var stroke = Math.Max(2, diameter * 0.14);
        var glowWidth = stroke * (1 + 3 * look.Glow);
        var size = (int)Math.Ceiling(diameter + glowWidth) + 4;
        return Draw(size, size, g =>
        {
            var offset = (float)((size - diameter) / 2);
            var rect = new RectangleF(offset, offset, (float)diameter, (float)diameter);
            if (look.Glow > 0)
            {
                for (var i = 3; i >= 1; i--)
                {
                    using var glow = new Pen(Color.FromArgb(Alpha(0.22 * look.Glow * look.Opacity), look.Color), (float)(stroke + (glowWidth - stroke) * i / 3));
                    g.DrawEllipse(glow, rect);
                }
            }
            using var pen = new Pen(Color.FromArgb(Alpha(look.Opacity), look.Color), (float)stroke);
            g.DrawEllipse(pen, rect);
        });
    }

    /// <summary>A soft-edged hole in the dimming layer: transparent inside, dim outside.</summary>
    static Sprite RenderSpotlight(PointerLook look)
    {
        var radius = Math.Max(10, look.SpotlightRadius);
        var feather = Math.Max(2, radius * 0.08);
        var dim = (uint)Alpha(look.SpotlightDim);
        var size = (int)Math.Ceiling((radius + feather) * 2) + 2;
        var center = size / 2.0;
        var pixels = new uint[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x + 0.5 - center;
                var dy = y + 0.5 - center;
                var t = Math.Clamp((Math.Sqrt(dx * dx + dy * dy) - (radius - feather)) / (2 * feather), 0, 1);
                var alpha = (uint)Math.Round(dim * t * t * (3 - 2 * t));
                pixels[y * size + x] = alpha << 24; // black, premultiplied
            }
        }
        return new Sprite(size, size, pixels, dim << 24);
    }

    static void FillSoftCircle(Graphics g, float center, float radius, Color color)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(center - radius, center - radius, radius * 2, radius * 2);
        using var brush = new PathGradientBrush(path)
        {
            CenterPoint = new PointF(center, center),
            CenterColor = color,
            SurroundColors = [Color.FromArgb(0, color)],
            // From the edge (0) to the center (1): mostly faint, rising towards the middle.
            Blend = new Blend { Positions = [0f, 0.5f, 1f], Factors = [0f, 0.35f, 1f] },
        };
        g.FillPath(brush, path);
    }

    static unsafe Sprite Draw(int width, int height, Action<Graphics> paint)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);
            paint(g);
        }

        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var pixels = new uint[width * height];
            for (var y = 0; y < height; y++)
                new ReadOnlySpan<uint>((byte*)data.Scan0 + (long)y * data.Stride, width).CopyTo(pixels.AsSpan(y * width, width));
            return new Sprite(width, height, pixels, 0);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    static int Alpha(double fraction) => (int)Math.Round(Math.Clamp(fraction, 0, 1) * 255);

    static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}
