using Spotlight.Core;
using SpotlightImproved.Overlay;

namespace SpotlightImproved;

/// <summary>
/// Turns pointer events from the Spotlight into the on-screen pointer. Device events arrive on HID threads;
/// motion is applied there under a lock and the UI thread is asked to redraw (coalesced, at most one pending).
/// </summary>
sealed class PointerPresenter : IDisposable
{
    readonly SynchronizationContext _ui;
    readonly Func<AppSettings> _settings;
    readonly Dictionary<string, OverlayWindow> _overlays = [];
    readonly PointerMotion _motion = new();
    readonly Lock _lock = new();
    readonly System.Windows.Forms.Timer _animation = new() { Interval = 15 };
    bool _redrawPosted;
    OverlayWindow? _active;
    (PointerLook Look, Sprite Sprite)? _sprite;
    (double U, double V)? _lastPosition;
    long _animationStart;
    Action<double>? _animate; // called with elapsed ms; set while fading or previewing

    public PointerPresenter(SynchronizationContext ui, Func<AppSettings> settings)
    {
        _ui = ui;
        _settings = settings;
        _animation.Tick += (_, _) => _animate?.Invoke(Environment.TickCount64 - _animationStart);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>Screen used for the last pointer, for the tray tooltip.</summary>
    public string? LastScreen { get; private set; }

    // --- HID threads ---

    public void OnPressed() => _ui.Post(_ => Show(_settings()), null);

    public void OnMoved(int dx, int dy)
    {
        lock (_lock)
        {
            _motion.Apply(dx, dy);
            if (_redrawPosted)
                return;
            _redrawPosted = true;
        }
        _ui.Post(_ => Redraw(), null);
    }

    public void OnReleased() => _ui.Post(_ => Hide(), null);

    // --- UI thread ---

    /// <summary>Shows the pointer on the target screen for a moment, circling, with the given settings.</summary>
    public void Preview(AppSettings settings)
    {
        Show(settings);
        if (_active is not { } overlay)
            return;

        var center = overlay.Bounds.Location + overlay.Bounds.Size / 2;
        var radius = overlay.Bounds.Height / 8.0;
        StartAnimation(elapsed =>
        {
            if (elapsed > 2000)
            {
                StopAnimation();
                overlay.Hide(); // not Hide(): a preview must not change the remembered position
                return;
            }
            var angle = elapsed / 2000 * 2 * Math.PI;
            lock (_lock)
                _motion.MoveTo(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle) + radius);
            Redraw();
        });
    }

    void Show(AppSettings settings)
    {
        StopAnimation();
        var (screen, reason) = ScreenLocator.Find(settings.TargetScreen);
        LastScreen = $"{ScreenLocator.Describe(screen)} ({reason})";

        var overlay = GetOverlay(screen);
        if (_active is not null && _active != overlay)
            _active.Hide();
        _active = overlay;

        var bounds = screen.Bounds;
        var scale = bounds.Height / 1080.0;
        var look = PointerLook.From(settings, scale);
        if (_sprite?.Look != look)
            _sprite = (look, SpriteRenderer.Render(look));

        Point position;
        lock (_lock)
        {
            _motion.Sensitivity = settings.Sensitivity;
            _motion.Acceleration = settings.Acceleration;
            _motion.Scale = scale;
            _motion.SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            var (u, v) = settings.StartPosition == PointerStart.LastPosition && _lastPosition is { } last ? last : (0.5, 0.5);
            _motion.PlaceRelative(u, v);
            position = CurrentPosition();
        }

        overlay.Show(_sprite.Value.Sprite, position);
    }

    void Redraw()
    {
        Point position;
        lock (_lock)
        {
            _redrawPosted = false;
            position = CurrentPosition();
        }
        _active?.Move(position);
    }

    void Hide()
    {
        if (_active is not { IsShown: true } overlay)
            return;

        lock (_lock)
            _lastPosition = _motion.Relative;

        var fade = _settings().FadeOutMs;
        if (fade <= 0 || _animate is not null)
        {
            StopAnimation();
            overlay.Hide();
            return;
        }

        StartAnimation(elapsed =>
        {
            if (elapsed >= fade)
            {
                StopAnimation();
                overlay.Hide();
            }
            else
            {
                overlay.SetOpacity((byte)(255 * (1 - elapsed / fade)));
            }
        });
    }

    void StartAnimation(Action<double> step)
    {
        _animationStart = Environment.TickCount64;
        _animate = step;
        _animation.Start();
    }

    void StopAnimation()
    {
        _animation.Stop();
        _animate = null;
    }

    Point CurrentPosition() => new((int)Math.Round(_motion.X), (int)Math.Round(_motion.Y));

    OverlayWindow GetOverlay(Screen screen)
    {
        if (_overlays.TryGetValue(screen.DeviceName, out var existing))
        {
            if (existing.Bounds == screen.Bounds)
                return existing;
            existing.Dispose();
        }

        var overlay = new OverlayWindow(screen.Bounds);
        _overlays[screen.DeviceName] = overlay;
        return overlay;
    }

    // Screens were added, removed or resized: rebuild overlays lazily on the next press.
    void OnDisplaySettingsChanged(object? sender, EventArgs e) => _ui.Post(_ => DisposeOverlays(), null);

    void DisposeOverlays()
    {
        StopAnimation();
        foreach (var overlay in _overlays.Values)
            overlay.Dispose();
        _overlays.Clear();
        _active = null;
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        DisposeOverlays();
        _animation.Dispose();
    }
}
