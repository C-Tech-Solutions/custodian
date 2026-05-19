using System.IO;
using System.Text.Json;

namespace Custodian.App.Services;

public sealed class UiSettings
{
    public string Theme { get; set; } = "Light";
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 880;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; }
    public double LeftPanelWidth { get; set; } = 320;
    public double RightPanelWidth { get; set; } = 350;
    public bool RightPanelCollapsed { get; set; }
    public string ChartMode { get; set; } = "Treemap";
    public string LastPath { get; set; } = string.Empty;
    public List<string> RecentPaths { get; set; } = [];
}

public static class UiSettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Custodian",
        "ui.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static UiSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new UiSettings();
            }
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<UiSettings>(json) ?? new UiSettings();
        }
        catch
        {
            return new UiSettings();
        }
    }

    public static void Save(UiSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // Settings are best-effort; don't crash on disk full / permission errors.
        }
    }
}
