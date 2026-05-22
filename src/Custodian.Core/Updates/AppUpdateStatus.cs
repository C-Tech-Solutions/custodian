namespace Custodian.Core.Updates;

public enum AppUpdateStatusKind
{
    NotInstalled,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToRestart,
    Failed
}

public sealed record AppUpdateStatus(
    AppUpdateStatusKind Kind,
    string Message,
    string? CurrentVersion = null,
    string? AvailableVersion = null,
    int? ProgressPercent = null);

public static class AppUpdateStatusFactory
{
    public static AppUpdateStatus NotInstalled() =>
        new(AppUpdateStatusKind.NotInstalled, "Updates are available only from the installed Custodian app.");

    public static AppUpdateStatus Checking() =>
        new(AppUpdateStatusKind.Checking, "Checking for updates...");

    public static AppUpdateStatus UpToDate(string? currentVersion) =>
        new(AppUpdateStatusKind.UpToDate, VersionMessage("Custodian is up to date.", currentVersion), currentVersion);

    public static AppUpdateStatus Available(string availableVersion, string? currentVersion) =>
        new(
            AppUpdateStatusKind.Available,
            string.IsNullOrWhiteSpace(currentVersion)
                ? $"Custodian {availableVersion} is available."
                : $"Custodian {availableVersion} is available. Current version: {currentVersion}.",
            currentVersion,
            availableVersion);

    public static AppUpdateStatus Downloading(string availableVersion, int progressPercent)
    {
        var clampedProgress = Math.Clamp(progressPercent, 0, 100);
        return new(
            AppUpdateStatusKind.Downloading,
            $"Downloading Custodian {availableVersion}: {clampedProgress}%",
            AvailableVersion: availableVersion,
            ProgressPercent: clampedProgress);
    }

    public static AppUpdateStatus ReadyToRestart(string availableVersion) =>
        new(AppUpdateStatusKind.ReadyToRestart, $"Custodian {availableVersion} is ready to install.", AvailableVersion: availableVersion);

    public static AppUpdateStatus Failed(string message) =>
        new(AppUpdateStatusKind.Failed, $"Update check failed: {message}");

    private static string VersionMessage(string message, string? currentVersion) =>
        string.IsNullOrWhiteSpace(currentVersion) ? message : $"{message} Current version: {currentVersion}.";
}
