using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpotlightImproved;

public enum PointerStyle
{
    Dot,
    Ring,
    Spotlight,
}

public enum PointerStart
{
    Center,
    LastPosition,
}

public enum TapAction
{
    Nothing,
    LeftClick,
}

public enum LongPressAction
{
    ChangeSlide,
    Nothing,
}

/// <summary>User settings, edited in a PropertyGrid and stored as JSON in %APPDATA%\SpotlightImproved.</summary>
public sealed class AppSettings
{
    public const string AutoScreen = "auto";

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotlightImproved", "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Category("Pointer"), DisplayName("Style")]
    [Description("Dot = laser dot, Ring = outlined circle, Spotlight = dims everything except a circle.")]
    public PointerStyle Style { get; set; } = PointerStyle.Dot;

    [Category("Pointer"), DisplayName("Color")]
    [JsonIgnore]
    public Color Color
    {
        get => ColorTranslator.FromHtml(ColorHtml);
        set => ColorHtml = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
    }

    [Browsable(false)]
    public string ColorHtml { get; set; } = "#FF1E1E";

    [Category("Pointer"), DisplayName("Size")]
    [Description("Diameter of the dot or ring in pixels on a 1080p screen. Scaled for other resolutions.")]
    public int Size { get; set; } = 26;

    [Category("Pointer"), DisplayName("Opacity (%)")]
    public int Opacity { get; set; } = 90;

    [Category("Pointer"), DisplayName("Glow (%)")]
    [Description("Soft halo around the dot or ring. 0 = none.")]
    public int Glow { get; set; } = 60;

    [Category("Spotlight"), DisplayName("Radius")]
    [Description("Radius of the bright circle in pixels on a 1080p screen.")]
    public int SpotlightRadius { get; set; } = 170;

    [Category("Spotlight"), DisplayName("Dimming (%)")]
    [Description("How dark the rest of the screen gets.")]
    public int SpotlightDim { get; set; } = 65;

    [Category("Movement"), DisplayName("Sensitivity")]
    [Description("How far the pointer travels for a given wrist movement.")]
    public double Sensitivity { get; set; } = 1.0;

    [Category("Movement"), DisplayName("Acceleration")]
    [Description("0 = linear. Higher values make quick movements travel further, for precise slow aiming.")]
    public double Acceleration { get; set; } = 0.3;

    [Category("Movement"), DisplayName("Start position")]
    [Description("Where the pointer appears when you press the button.")]
    public PointerStart StartPosition { get; set; } = PointerStart.Center;

    [Category("Buttons"), DisplayName("Quick press of pointer button")]
    [Description("Nothing = ignored. Left click = the remote's default, which advances the slide.")]
    public TapAction PointerTap { get; set; } = TapAction.Nothing;

    [Category("Buttons"), DisplayName("Long press of Next / Back")]
    [Description("Change slide = same as a short press, however long the button is held. Nothing = the remote's default.")]
    public LongPressAction LongPressNextBack { get; set; } = LongPressAction.ChangeSlide;

    [Category("Behavior"), DisplayName("Fade out (ms)")]
    [Description("0 = the pointer disappears the instant the button is released.")]
    public int FadeOutMs { get; set; }

    /// <summary>"auto" or a screen device name such as \\.\DISPLAY2. Chosen from the tray menu.</summary>
    [Browsable(false)]
    public string TargetScreen { get; set; } = AutoScreen;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Normalize()
    {
        Size = Math.Clamp(Size, 4, 300);
        Opacity = Math.Clamp(Opacity, 10, 100);
        Glow = Math.Clamp(Glow, 0, 100);
        SpotlightRadius = Math.Clamp(SpotlightRadius, 30, 1000);
        SpotlightDim = Math.Clamp(SpotlightDim, 10, 95);
        Sensitivity = Math.Clamp(Sensitivity, 0.1, 10);
        Acceleration = Math.Clamp(Acceleration, 0, 5);
        FadeOutMs = Math.Clamp(FadeOutMs, 0, 1000);
        try
        {
            _ = ColorTranslator.FromHtml(ColorHtml);
        }
        catch (Exception)
        {
            ColorHtml = "#FF1E1E";
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) is { } loaded)
            {
                loaded.Normalize();
                return loaded;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable settings: start from defaults rather than refusing to run.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
