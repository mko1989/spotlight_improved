namespace Spotlight.Core.Tests;

public class PointerMotionTests
{
    static PointerMotion OnSecondScreen()
    {
        // Presentation screen to the right of a 1920-wide laptop screen.
        var motion = new PointerMotion();
        motion.SetBounds(1920, 0, 1920, 1080);
        motion.PlaceRelative(0.5, 0.5);
        return motion;
    }

    [Fact]
    public void Starts_at_center()
    {
        var motion = OnSecondScreen();

        Assert.Equal(2880, motion.X);
        Assert.Equal(540, motion.Y);
    }

    [Fact]
    public void Never_leaves_the_screen()
    {
        var motion = OnSecondScreen();

        motion.Apply(-30000, -30000);
        Assert.Equal((1920.0, 0.0), (motion.X, motion.Y));

        motion.Apply(30000, 30000);
        Assert.Equal((3839.0, 1079.0), (motion.X, motion.Y));
    }

    [Fact]
    public void Applies_sensitivity_and_scale()
    {
        var motion = OnSecondScreen();
        motion.Sensitivity = 2;
        motion.Scale = 1.5;

        motion.Apply(10, -4);

        Assert.Equal(2880 + 30, motion.X, 6);
        Assert.Equal(540 - 12, motion.Y, 6);
    }

    [Fact]
    public void Acceleration_moves_fast_motion_further()
    {
        var slow = OnSecondScreen();
        var fast = OnSecondScreen();
        slow.Acceleration = fast.Acceleration = 1;

        for (var i = 0; i < 10; i++)
            slow.Apply(2, 0);
        fast.Apply(20, 0);

        Assert.True(fast.X - 2880 > slow.X - 2880);
    }

    [Fact]
    public void Relative_position_maps_between_screens()
    {
        var motion = OnSecondScreen();
        motion.Apply(480, 270);
        var (u, v) = motion.Relative;

        motion.SetBounds(0, 0, 3840, 2160);
        motion.PlaceRelative(u, v);

        Assert.Equal(2880, motion.X, 6);
        Assert.Equal(1620, motion.Y, 6);
    }
}
