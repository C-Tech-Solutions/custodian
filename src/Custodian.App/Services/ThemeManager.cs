using System.Windows;
using WpfColor = System.Windows.Media.Color;

namespace Custodian.App.Services;

public enum AppTheme
{
    Light,
    Dark,
    Ocean,
    Forest,
    Ember
}

public static class ThemeManager
{
    private sealed record ThemeDefinition(
        string PalettePath,
        bool UsesDarkChrome,
        WpfColor CaptionColor,
        WpfColor CaptionTextColor);

    private static readonly AppTheme[] ThemeOrder =
    [
        AppTheme.Dark,
        AppTheme.Light,
        AppTheme.Ocean,
        AppTheme.Forest,
        AppTheme.Ember
    ];

    private static readonly Dictionary<AppTheme, ThemeDefinition> ThemeDefinitions = new()
    {
        [AppTheme.Light] = new(
            "/Custodian.App;component/Themes/Palette.Light.xaml",
            false,
            WpfColor.FromRgb(246, 247, 249),
            WpfColor.FromRgb(15, 23, 42)),
        [AppTheme.Dark] = new(
            "/Custodian.App;component/Themes/Palette.Dark.xaml",
            true,
            WpfColor.FromRgb(15, 17, 21),
            WpfColor.FromRgb(243, 245, 249)),
        [AppTheme.Ocean] = new(
            "/Custodian.App;component/Themes/Palette.Ocean.xaml",
            true,
            WpfColor.FromRgb(7, 18, 20),
            WpfColor.FromRgb(230, 255, 250)),
        [AppTheme.Forest] = new(
            "/Custodian.App;component/Themes/Palette.Forest.xaml",
            true,
            WpfColor.FromRgb(16, 22, 17),
            WpfColor.FromRgb(238, 247, 236)),
        [AppTheme.Ember] = new(
            "/Custodian.App;component/Themes/Palette.Ember.xaml",
            true,
            WpfColor.FromRgb(21, 18, 17),
            WpfColor.FromRgb(255, 247, 237))
    };

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static IReadOnlyList<AppTheme> AvailableThemes => ThemeOrder;

    public static bool UsesDarkChrome => GetDefinition(Current).UsesDarkChrome;

    public static WpfColor CaptionColor => GetDefinition(Current).CaptionColor;

    public static WpfColor CaptionTextColor => GetDefinition(Current).CaptionTextColor;

    public static event EventHandler<AppTheme>? ThemeChanged;

    public static AppTheme ParseOrDefault(string? value)
    {
        return TryParse(value, out var theme) ? theme : AppTheme.Dark;
    }

    public static bool TryParse(string? value, out AppTheme theme)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Enum.TryParse(value, ignoreCase: true, out theme)
            && ThemeDefinitions.ContainsKey(theme))
        {
            return true;
        }

        theme = AppTheme.Dark;
        return false;
    }

    public static void Apply(AppTheme theme)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        if (!ThemeDefinitions.ContainsKey(theme))
        {
            theme = AppTheme.Dark;
        }

        var definition = GetDefinition(theme);
        if (theme == Current && IsPaletteLoaded(app, definition))
        {
            return;
        }

        var newPalette = new ResourceDictionary { Source = new Uri(definition.PalettePath, UriKind.Relative) };

        // Remove existing palettes (anything that looks like Palette.*.xaml).
        var toRemove = app.Resources.MergedDictionaries
            .Where(dict => dict.Source is not null
                && dict.Source.OriginalString.Contains("Palette.", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var dict in toRemove)
        {
            app.Resources.MergedDictionaries.Remove(dict);
        }

        // Insert at the start so Controls.xaml DynamicResource lookups resolve against it.
        app.Resources.MergedDictionaries.Insert(0, newPalette);

        Current = theme;
        ThemeChanged?.Invoke(null, theme);
    }

    public static void Toggle()
    {
        Apply(Current == AppTheme.Light ? AppTheme.Dark : AppTheme.Light);
    }

    private static ThemeDefinition GetDefinition(AppTheme theme)
    {
        return ThemeDefinitions.TryGetValue(theme, out var definition)
            ? definition
            : ThemeDefinitions[AppTheme.Dark];
    }

    private static bool IsPaletteLoaded(System.Windows.Application app, ThemeDefinition definition)
    {
        return app.Resources.MergedDictionaries.Any(dict =>
            dict.Source is not null
            && string.Equals(dict.Source.OriginalString, definition.PalettePath, StringComparison.OrdinalIgnoreCase));
    }
}
