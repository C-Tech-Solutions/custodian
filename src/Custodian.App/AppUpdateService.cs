using System.Reflection;
using Custodian.Core.Updates;
using Velopack;
using Velopack.Sources;

namespace Custodian.App;

internal sealed class AppUpdateService
{
    private const string RepositoryUrl = "https://github.com/ctech1313/custodian";
    private const string UpdateSourceOverrideVariable = "CUSTODIAN_UPDATE_SOURCE";
    private readonly UpdateManager _manager;

    public AppUpdateService()
        : this(CreateUpdateManager())
    {
    }

    private AppUpdateService(UpdateManager manager)
    {
        _manager = manager;
    }

    public async Task<AppUpdateCheckResult> CheckForUpdatesAsync()
    {
        var currentVersion = CurrentVersionText();

        if (!_manager.IsInstalled)
        {
            return AppUpdateCheckResult.NotInstalled(AppUpdateStatusFactory.NotInstalled());
        }

        if (_manager.UpdatePendingRestart is { } pending)
        {
            return AppUpdateCheckResult.ReadyToRestart(
                pending,
                AppUpdateStatusFactory.ReadyToRestart(pending.Version?.ToString() ?? "the latest version"));
        }

        var updateInfo = await _manager.CheckForUpdatesAsync();
        if (updateInfo is null)
        {
            return AppUpdateCheckResult.UpToDate(AppUpdateStatusFactory.UpToDate(currentVersion));
        }

        var availableVersion = updateInfo.TargetFullRelease.Version?.ToString() ?? "the latest version";
        return AppUpdateCheckResult.Available(
            updateInfo,
            AppUpdateStatusFactory.Available(availableVersion, currentVersion));
    }

    public Task DownloadUpdatesAsync(AppUpdateCheckResult update, IProgress<AppUpdateStatus> progress, CancellationToken cancellationToken)
    {
        if (update.UpdateInfo is null)
        {
            throw new InvalidOperationException("No update is available to download.");
        }

        var availableVersion = update.Status.AvailableVersion ?? update.UpdateInfo.TargetFullRelease.Version?.ToString() ?? "the latest version";
        return _manager.DownloadUpdatesAsync(
            update.UpdateInfo,
            percent => progress.Report(AppUpdateStatusFactory.Downloading(availableVersion, percent)),
            ignoreDeltas: false,
            cancellationToken);
    }

    public void ApplyUpdatesAndRestart(AppUpdateCheckResult update)
    {
        var asset = update.PendingRestartAsset ?? update.UpdateInfo?.TargetFullRelease;
        if (asset is null)
        {
            throw new InvalidOperationException("No downloaded update is ready to install.");
        }

        _manager.WaitExitThenApplyUpdates(asset, silent: true, restart: true, Array.Empty<string>());
    }

    private static UpdateManager CreateUpdateManager()
    {
        var options = new UpdateOptions
        {
            ExplicitChannel = "win"
        };

        var sourceOverride = Environment.GetEnvironmentVariable(UpdateSourceOverrideVariable);
        if (!string.IsNullOrWhiteSpace(sourceOverride))
        {
            return new UpdateManager(sourceOverride, options, logger: null!, locator: null!);
        }

        var source = new GithubSource(RepositoryUrl, accessToken: null!, prerelease: false, downloader: null!);

        return new UpdateManager(source, options, logger: null!, locator: null!);
    }

    private string? CurrentVersionText() =>
        _manager.CurrentVersion?.ToString()
        ?? Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
}

internal sealed record AppUpdateCheckResult(
    AppUpdateStatus Status,
    UpdateInfo? UpdateInfo = null,
    VelopackAsset? PendingRestartAsset = null)
{
    public static AppUpdateCheckResult NotInstalled(AppUpdateStatus status) => new(status);

    public static AppUpdateCheckResult UpToDate(AppUpdateStatus status) => new(status);

    public static AppUpdateCheckResult Available(UpdateInfo updateInfo, AppUpdateStatus status) => new(status, updateInfo);

    public static AppUpdateCheckResult ReadyToRestart(VelopackAsset pendingAsset, AppUpdateStatus status) => new(status, PendingRestartAsset: pendingAsset);
}
