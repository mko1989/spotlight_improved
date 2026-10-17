namespace SpotlightImproved;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var instance = new Mutex(initiallyOwned: true, @"Local\SpotlightImproved", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Spotlight Improved is already running - look for its icon in the notification area.", "Spotlight Improved");
            return;
        }

        // Per-monitor DPI awareness: all window and screen coordinates are physical pixels.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.Run(new TrayApp());
    }
}
