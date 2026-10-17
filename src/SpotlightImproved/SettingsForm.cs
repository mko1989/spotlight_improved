namespace SpotlightImproved;

/// <summary>Settings dialog. Edits a copy; the caller applies it when the dialog returns OK.</summary>
sealed class SettingsForm : Form
{
    public SettingsForm(AppSettings working, Action<AppSettings> preview)
    {
        Text = "Spotlight Improved - Settings";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 520);
        MinimumSize = new Size(360, 400);
        ShowInTaskbar = true;
        Icon = AppIcon.Instance;

        var grid = new PropertyGrid
        {
            Dock = DockStyle.Fill,
            SelectedObject = working,
            ToolbarVisible = false,
            PropertySort = PropertySort.Categorized,
        };
        grid.PropertyValueChanged += (_, _) =>
        {
            working.Normalize();
            grid.Refresh();
        };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var previewButton = new Button { Text = "Preview", AutoSize = true };
        previewButton.Click += (_, _) => preview(working);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8),
        };
        buttons.Controls.AddRange([cancel, ok, previewButton]);

        Controls.Add(grid);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
