namespace Spotlight.Core;

/// <summary>
/// Turns raw motion deltas into a pointer position that can never leave its bounds (the presentation screen).
/// </summary>
public sealed class PointerMotion
{
    double _left, _top, _width = 1, _height = 1;

    /// <summary>Base multiplier applied to every delta.</summary>
    public double Sensitivity { get; set; } = 1.0;

    /// <summary>0 = linear. Higher values make fast movements travel further than slow ones.</summary>
    public double Acceleration { get; set; }

    /// <summary>Resolution factor, so the same wrist movement covers the same share of any screen.</summary>
    public double Scale { get; set; } = 1.0;

    public double X { get; private set; }
    public double Y { get; private set; }

    /// <summary>Position as a fraction of the bounds (0..1), for restoring it on another screen size.</summary>
    public (double U, double V) Relative => ((X - _left) / _width, (Y - _top) / _height);

    public void SetBounds(double left, double top, double width, double height)
    {
        _left = left;
        _top = top;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        MoveTo(X, Y);
    }

    public void PlaceRelative(double u, double v) => MoveTo(_left + u * _width, _top + v * _height);

    public void MoveTo(double x, double y)
    {
        X = Math.Clamp(x, _left, _left + _width - 1);
        Y = Math.Clamp(y, _top, _top + _height - 1);
    }

    public void Apply(int dx, int dy)
    {
        var speed = Math.Sqrt(dx * dx + dy * dy);
        var gain = Sensitivity * Scale * (1 + Acceleration * speed / 10.0);
        MoveTo(X + dx * gain, Y + dy * gain);
    }
}
