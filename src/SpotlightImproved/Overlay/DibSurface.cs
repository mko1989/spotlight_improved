using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SpotlightImproved.Overlay;

/// <summary>A 32-bit premultiplied BGRA bitmap selected into a memory DC, as UpdateLayeredWindow expects.</summary>
sealed unsafe class DibSurface : IDisposable
{
    readonly IntPtr _bitmap;
    readonly IntPtr _previous;
    readonly uint* _bits;

    public DibSurface(int width, int height)
    {
        Width = width;
        Height = height;
        var header = new Gdi32.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Gdi32.BITMAPINFOHEADER),
            biWidth = width,
            biHeight = -height, // negative = top-down rows
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Gdi32.BI_RGB,
        };

        Hdc = Gdi32.CreateCompatibleDC(IntPtr.Zero);
        _bitmap = Gdi32.CreateDIBSection(Hdc, ref header, Gdi32.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            Gdi32.DeleteDC(Hdc);
            throw new Win32Exception(error, $"Cannot create a {width}x{height} overlay bitmap");
        }
        _bits = (uint*)bits;
        _previous = Gdi32.SelectObject(Hdc, _bitmap);
    }

    public int Width { get; }
    public int Height { get; }
    public IntPtr Hdc { get; }
    public Rectangle Bounds => new(0, 0, Width, Height);

    Span<uint> Row(int y) => new(_bits + (long)y * Width, Width);

    public void Fill(uint pixel) => new Span<uint>(_bits, Width * Height).Fill(pixel);

    public void Fill(Rectangle area, uint pixel)
    {
        area.Intersect(Bounds);
        for (var y = area.Top; y < area.Bottom; y++)
            Row(y).Slice(area.X, area.Width).Fill(pixel);
    }

    /// <summary>Copies the sprite with its top-left corner at (x, y), clipped. Returns the area written.</summary>
    public Rectangle Blit(Sprite sprite, int x, int y)
    {
        var target = new Rectangle(x, y, sprite.Width, sprite.Height);
        target.Intersect(Bounds);
        for (var row = target.Top; row < target.Bottom; row++)
        {
            sprite.Pixels.AsSpan((row - y) * sprite.Width + (target.X - x), target.Width)
                .CopyTo(Row(row).Slice(target.X, target.Width));
        }
        return target;
    }

    public void Dispose()
    {
        Gdi32.SelectObject(Hdc, _previous);
        Gdi32.DeleteObject(_bitmap);
        Gdi32.DeleteDC(Hdc);
    }
}
