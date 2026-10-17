using static SpotlightImproved.User32;

namespace SpotlightImproved.Overlay;

/// <summary>
/// A click-through, never-activated, topmost layered window covering exactly one screen. The pointer is drawn
/// into it, so it is clipped to that screen by construction and can never show up on the presenter view.
/// Moving the pointer only re-uploads the small area that changed.
/// </summary>
sealed unsafe class OverlayWindow : NativeWindow, IDisposable
{
    readonly DibSurface _surface;
    Sprite? _sprite;
    Rectangle _drawn;
    byte _opacity = 255;

    public OverlayWindow(Rectangle bounds)
    {
        Bounds = bounds;
        _surface = new DibSurface(bounds.Width, bounds.Height);
        CreateHandle(new CreateParams
        {
            Caption = "Spotlight Improved pointer",
            Style = WS_POPUP,
            ExStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
        });
    }

    /// <summary>Screen area covered, in physical pixels.</summary>
    public Rectangle Bounds { get; }

    public bool IsShown { get; private set; }

    public void Show(Sprite sprite, Point position)
    {
        _sprite = sprite;
        _opacity = 255;
        _surface.Fill(sprite.Background);
        _drawn = Place(sprite, position);
        Commit(null);
        // Re-assert topmost every time: the slideshow window may have been raised since we were last shown.
        SetWindowPos(Handle, HWND_TOPMOST, Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        IsShown = true;
    }

    public void Move(Point position)
    {
        if (!IsShown || _sprite is null)
            return;

        var previous = _drawn;
        _surface.Fill(previous, _sprite.Background);
        _drawn = Place(_sprite, position);

        var dirty = previous.IsEmpty ? _drawn : _drawn.IsEmpty ? previous : Rectangle.Union(previous, _drawn);
        if (!dirty.IsEmpty)
            Commit(dirty);
    }

    /// <summary>Changes the whole window's opacity without re-uploading pixels (used for fading out).</summary>
    public void SetOpacity(byte opacity)
    {
        if (!IsShown)
            return;
        _opacity = opacity;
        var blend = Blend(opacity);
        if (!UpdateLayeredWindow(Handle, IntPtr.Zero, null, null, IntPtr.Zero, null, 0, &blend, ULW_ALPHA))
            Commit(null);
    }

    public void Hide()
    {
        if (!IsShown)
            return;
        ShowWindow(Handle, SW_HIDE);
        IsShown = false;
    }

    Rectangle Place(Sprite sprite, Point screenPosition)
        => _surface.Blit(sprite, screenPosition.X - Bounds.X - sprite.Width / 2, screenPosition.Y - Bounds.Y - sprite.Height / 2);

    void Commit(Rectangle? dirty)
    {
        var destination = new POINT { X = Bounds.X, Y = Bounds.Y };
        var size = new SIZE { Width = Bounds.Width, Height = Bounds.Height };
        var source = new POINT();
        var blend = Blend(_opacity);
        var dirtyRect = dirty is { } d ? new RECT { Left = d.Left, Top = d.Top, Right = d.Right, Bottom = d.Bottom } : default;

        var info = new UPDATELAYEREDWINDOWINFO
        {
            cbSize = sizeof(UPDATELAYEREDWINDOWINFO),
            pptDst = &destination,
            psize = &size,
            hdcSrc = _surface.Hdc,
            pptSrc = &source,
            pblend = &blend,
            dwFlags = ULW_ALPHA,
            prcDirty = dirty is null ? null : &dirtyRect,
        };

        if (!UpdateLayeredWindowIndirect(Handle, &info) && dirty is not null)
        {
            info.prcDirty = null;
            UpdateLayeredWindowIndirect(Handle, &info);
        }
    }

    static BLENDFUNCTION Blend(byte opacity) => new()
    {
        BlendOp = AC_SRC_OVER,
        SourceConstantAlpha = opacity,
        AlphaFormat = AC_SRC_ALPHA,
    };

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_NCHITTEST:
                m.Result = HTTRANSPARENT;
                return;
            case WM_MOUSEACTIVATE:
                m.Result = MA_NOACTIVATE;
                return;
            case WM_DPICHANGED:
                m.Result = IntPtr.Zero; // keep our physical-pixel bounds; Windows must not resize us
                return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        DestroyHandle();
        _surface.Dispose();
    }
}
