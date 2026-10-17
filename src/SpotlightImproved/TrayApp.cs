using Microsoft.Win32;
using Spotlight.Core;

namespace SpotlightImproved;

/// <summary>Notification-area app: owns the device controller, the pointer and the menu.</summary>
sealed class TrayApp : ApplicationContext
{
    readonly SynchronizationContext _ui;
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _status = new() { Enabled = false };
    readonly ToolStripMenuItem _styleMenu = new("Pointer style");
    readonly ToolStripMenuItem _screenMenu = new("Presentation screen");
    readonly ToolStripMenuItem _autostart = new("Start with Windows");
    readonly SpotlightController _controller;
    readonly PointerPresenter _presenter;
    AppSettings _settings;
    SettingsForm? _settingsForm;
    bool _exiting;

    public TrayApp()
    {
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Must be created on the UI thread.");
        _settings = AppSettings.Load();
        _presenter = new PointerPresenter(_ui, () => _settings);

        _controller = new SpotlightController(WindowsConnector.TryConnect);
        _controller.PointerPressed += _presenter.OnPressed;
        _controller.PointerMoved += _presenter.OnMoved;
        _controller.PointerReleased += _presenter.OnReleased;
        _controller.ControlPressed += new ButtonActions(() => _settings).OnPressed;
        _controller.StateChanged += (state, detail) => _ui.Post(_ => ShowState(state, detail), null);

        foreach (var style in Enum.GetValues<PointerStyle>())
        {
            var item = new ToolStripMenuItem(style.ToString());
            item.Click += (_, _) =>
            {
                _settings.Style = style;
                Save();
            };
            _styleMenu.DropDownItems.Add(item);
        }
        _styleMenu.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem item in _styleMenu.DropDownItems)
                item.Checked = item.Text == _settings.Style.ToString();
        };

        _screenMenu.DropDownItems.Add(new ToolStripMenuItem("...")); // rebuilt on opening; needed for the submenu arrow
        _screenMenu.DropDownOpening += (_, _) => BuildScreenMenu();

        _autostart.Checked = Autostart.IsEnabled;
        _autostart.Click += (_, _) =>
        {
            Autostart.IsEnabled = !_autostart.Checked;
            _autostart.Checked = Autostart.IsEnabled;
        };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _status,
            new ToolStripSeparator(),
            _styleMenu,
            _screenMenu,
            new ToolStripMenuItem("Preview pointer", null, (_, _) => _presenter.Preview(_settings)),
            new ToolStripMenuItem("Settings...", null, (_, _) => OpenSettings()),
            new ToolStripSeparator(),
            _autostart,
            new ToolStripMenuItem("Exit", null, async (_, _) => await ExitAsync()),
        });

        _tray = new NotifyIcon { Icon = AppIcon.Instance, ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenSettings();
        ShowState(_controller.State, null);

        if (LogiOptions.IsRunning())
        {
            _tray.ShowBalloonTip(10000, "Logi Options+ is running",
                "It changes the same Spotlight settings. Quit Logi Options+ (and logioptionsplus_agent.exe) " +
                "for Spotlight Improved to work reliably.", ToolTipIcon.Warning);
        }

        SystemEvents.SessionEnding += OnSessionEnding;
        _controller.Start();
    }

    void ShowState(SpotlightState state, string? detail)
    {
        if (_exiting)
            return;

        var text = state switch
        {
            SpotlightState.Searching => "Spotlight not found - plug in the receiver",
            SpotlightState.Connecting => "Connecting to the Spotlight...",
            SpotlightState.Asleep => "Spotlight asleep - press a button to wake it",
            SpotlightState.Ready => "Spotlight ready",
            SpotlightState.Unsupported => $"Not supported: {detail}",
            _ => $"Problem: {detail}",
        };
        _status.Text = text;

        var tooltip = $"Spotlight Improved\n{text}";
        _tray.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        if (state is SpotlightState.Unsupported or SpotlightState.Error)
            _tray.ShowBalloonTip(8000, "Spotlight Improved", text, ToolTipIcon.Warning);
    }

    void BuildScreenMenu()
    {
        var items = _screenMenu.DropDownItems;
        items.Clear();

        var auto = new ToolStripMenuItem("Auto-detect (slide show / projector)") { Checked = _settings.TargetScreen == AppSettings.AutoScreen };
        auto.Click += (_, _) => SetTargetScreen(AppSettings.AutoScreen);
        items.Add(auto);
        items.Add(new ToolStripSeparator());

        foreach (var screen in Screen.AllScreens)
        {
            var item = new ToolStripMenuItem(ScreenLocator.Describe(screen)) { Checked = _settings.TargetScreen == screen.DeviceName };
            var deviceName = screen.DeviceName;
            item.Click += (_, _) => SetTargetScreen(deviceName);
            items.Add(item);
        }

        if (_presenter.LastScreen is { } last)
        {
            items.Add(new ToolStripSeparator());
            items.Add(new ToolStripMenuItem($"Last used: {last}") { Enabled = false });
        }
    }

    void SetTargetScreen(string target)
    {
        _settings.TargetScreen = target;
        Save();
    }

    void OpenSettings()
    {
        if (_settingsForm is not null)
        {
            _settingsForm.Activate();
            return;
        }

        var working = _settings.Clone();
        using var form = new SettingsForm(working, _presenter.Preview);
        _settingsForm = form;
        try
        {
            if (form.ShowDialog() == DialogResult.OK)
            {
                working.Normalize();
                _settings = working;
                Save();
            }
        }
        finally
        {
            _settingsForm = null;
        }
    }

    void Save()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _tray.ShowBalloonTip(5000, "Spotlight Improved", $"Could not save settings: {e.Message}", ToolTipIcon.Error);
        }
    }

    // Logging off or shutting down: give the button back to the device while we still can.
    void OnSessionEnding(object? sender, SessionEndingEventArgs e) => _controller.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));

    async Task ExitAsync()
    {
        if (_exiting)
            return;
        _exiting = true;
        _tray.Visible = false;
        SystemEvents.SessionEnding -= OnSessionEnding;

        await Task.WhenAny(_controller.DisposeAsync().AsTask(), Task.Delay(TimeSpan.FromSeconds(2)));
        _presenter.Dispose();
        _tray.Dispose();
        ExitThread();
    }
}
