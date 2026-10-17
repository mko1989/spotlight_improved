namespace SpotlightImproved;

/// <summary>Decides which screen the pointer lives on.</summary>
static class ScreenLocator
{
    // PowerPoint's slide show window. Its presenter view ("PodiumParent") is deliberately not matched.
    const string PowerPointSlideShowClass = "screenClass";

    static readonly HashSet<string> ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    /// <summary>
    /// Fixed screen if one is chosen and present; otherwise the screen showing a PowerPoint slide show, else the
    /// screen with a fullscreen foreground window, else the first non-primary screen (usually the projector).
    /// </summary>
    public static (Screen Screen, string Reason) Find(string targetScreen)
    {
        var screens = Screen.AllScreens;
        if (targetScreen != AppSettings.AutoScreen && screens.FirstOrDefault(s => s.DeviceName == targetScreen) is { } chosen)
            return (chosen, "chosen in the menu");

        if (screens.Length == 1)
            return (screens[0], "only screen");

        if (FindSlideShowWindow() is { } slideShow && FullscreenScreen(slideShow) is { } slideScreen)
            return (slideScreen, "PowerPoint slide show");

        var foreground = User32.GetForegroundWindow();
        if (foreground != IntPtr.Zero && IsCandidate(foreground) && !ShellClasses.Contains(ClassName(foreground))
            && FullscreenScreen(foreground) is { } foregroundScreen)
            return (foregroundScreen, "fullscreen window");

        return screens.FirstOrDefault(s => !s.Primary) is { } secondary
            ? (secondary, "secondary screen")
            : (Screen.PrimaryScreen ?? screens[0], "primary screen");
    }

    public static string Describe(Screen screen)
    {
        var index = Array.IndexOf(Screen.AllScreens, screen) + 1;
        return $"Screen {index} - {screen.Bounds.Width}x{screen.Bounds.Height}{(screen.Primary ? ", primary" : "")}";
    }

    static IntPtr? FindSlideShowWindow()
    {
        IntPtr found = IntPtr.Zero;
        User32.EnumWindows((hWnd, _) =>
        {
            if (IsCandidate(hWnd) && ClassName(hWnd) == PowerPointSlideShowClass)
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found == IntPtr.Zero ? null : found;
    }

    /// <summary>The screen the window fully covers, or null if it is not fullscreen.</summary>
    static Screen? FullscreenScreen(IntPtr hWnd)
    {
        if (!User32.GetWindowRect(hWnd, out var r))
            return null;
        var screen = Screen.FromHandle(hWnd);
        var b = screen.Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom ? screen : null;
    }

    static bool IsCandidate(IntPtr hWnd)
    {
        if (!User32.IsWindowVisible(hWnd) || User32.IsIconic(hWnd))
            return false;
        if (Dwmapi.DwmGetWindowAttribute(hWnd, Dwmapi.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false; // hidden UWP windows and windows on other virtual desktops
        User32.GetWindowThreadProcessId(hWnd, out var processId);
        return processId != Environment.ProcessId;
    }

    static string ClassName(IntPtr hWnd)
    {
        var buffer = new char[256];
        var length = User32.GetClassName(hWnd, buffer, buffer.Length);
        return new string(buffer, 0, length);
    }
}
