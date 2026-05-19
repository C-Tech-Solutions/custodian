using System.Windows;

namespace Custodian.App.Services;

public enum AppTheme
{
    Light,
    Dark
}

public static class ThemeManager
{
    private const string LightPalettePath = "/Custodian.App;component/Themes/Palette.Light.xaml";
    private const string DarkPalettePath = "/Custodian.App;component/Themes/Palette.Dark.xaml";

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    public static event EventHandler<AppTheme>? ThemeChanged;

    public static void Apply(AppTheme theme)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
        {
            return;
        }

        var path = theme == AppTheme.Dark ? DarkPalettePath : LightPalettePath;
        var newPalette = new ResourceDictionary { Source = new Uri(path, UriKind.Relative) };

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
}
