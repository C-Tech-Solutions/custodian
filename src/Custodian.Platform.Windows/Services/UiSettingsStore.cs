using System.IO;
using System.Text.Json;
using Custodian.Platform.Windows.Logging;
using Microsoft.Extensions.Logging;

namespace Custodian.Platform.Windows.Services;

internal sealed class UiSettings
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
    public bool AutoRelaunchAsAdministrator { get; set; }
    public bool AutoUpdateOnStartup { get; set; } = true;
    public string? LastSeenWhatsNewVersion { get; set; }
    public List<string> RecentPaths { get; set; } = [];
}

internal static class UiSettingsStore
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Custodian");

    private static readonly string FilePath = Path.Combine(AppDataDir, "ui.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    private static readonly ILogger Logger = AppLogging.CreateLogger(typeof(UiSettingsStore).FullName!);

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

    public static async Task SaveAsync(UiSettings settings)
    {
        try
        {
            await SaveCoreAsync(settings);
        }
        catch (Exception ex)
        {
            LogFailure("Save UI settings", ex);
            // Settings are best-effort; don't crash on disk full / permission errors.
        }
    }

    private static async Task SaveCoreAsync(UiSettings settings)
    {
        await SaveGate.WaitAsync();

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
                var json = JsonSerializer.Serialize(settings, Options);
                await File.WriteAllTextAsync(tempPath, json);
                File.Move(tempPath, FilePath, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // Best-effort cleanup; preserve any save failure from the try block.
                }
            }
        }
        finally
        {
            SaveGate.Release();
        }
    }

    private static void LogFailure(string operation, Exception ex) =>
        Logger.LogError(ex, "{Operation} failed.", operation);
}
