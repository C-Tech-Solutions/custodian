using System.IO;
using System.Diagnostics;
using System.Text.Json;

namespace Custodian.App.Services;

public sealed class UiSettings
{
    public string Theme { get; set; } = "Dark";
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
    public DateTime LastAutomaticUpdateCheckUtc { get; set; } = DateTime.MinValue;
    public List<string> RecentPaths { get; set; } = [];
}

public static class UiSettingsStore
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Custodian");

    private static readonly string FilePath = Path.Combine(AppDataDir, "ui.json");
    private static readonly string LogFilePath = Path.Combine(AppDataDir, "settings-errors.log");

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
        catch (Exception ex)
        {
            LogFailure("Load UI settings", ex);
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

            var tempPath = Path.Combine(
                dir ?? AppContext.BaseDirectory,
                $"{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, Options));
                File.Move(tempPath, FilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch (Exception ex)
        {
            LogFailure("Save UI settings", ex);
            // Settings are best-effort; don't crash on disk full / permission errors.
        }
    }

    private static void LogFailure(string operation, Exception ex)
    {
        Debug.WriteLine(ex);
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.AppendAllText(
                LogFilePath,
                $"{DateTimeOffset.UtcNow:O} {operation} failed{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch (Exception logEx)
        {
            Debug.WriteLine(logEx);
        }
    }
}
