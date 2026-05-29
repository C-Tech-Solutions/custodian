using System.Reflection;
using Custodian.App.Logging;
using Custodian.Core.Updates;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Custodian.App;

internal sealed class AppUpdateService
{
    private const string RepositoryUrl = "https://github.com/ctech1313/custodian";
    private const string UpdateSourceOverrideVariable = "CUSTODIAN_UPDATE_SOURCE";
    private const string UpdateChannelOverrideVariable = "CUSTODIAN_UPDATE_CHANNEL";
    private const string GitHubAccessTokenVariable = "CUSTODIAN_GITHUB_TOKEN";
    private const string GitHubPrereleaseVariable = "CUSTODIAN_UPDATE_PRERELEASES";
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

        _manager.WaitExitThenApplyUpdates(
            asset,
            silent: true,
            restart: true,
            Environment.GetCommandLineArgs().Skip(1).ToArray());
    }

    private static UpdateManager CreateUpdateManager()
    {
        var logger = AppLogging.CreateLogger("Custodian.App.Velopack");
        var locator = VelopackLocator.GetDefault(logger);
        var options = new UpdateOptions();
        var channelOverride = ReadEnvironmentValue(UpdateChannelOverrideVariable);
        if (channelOverride is not null)
        {
            options.ExplicitChannel = channelOverride;
        }

        var sourceOverride = ReadEnvironmentValue(UpdateSourceOverrideVariable);
        if (sourceOverride is not null)
        {
            return new UpdateManager(sourceOverride, options, logger, locator);
        }

        var channel = channelOverride ?? locator?.Channel;
        var accessToken = ReadEnvironmentValue(GitHubAccessTokenVariable) ?? string.Empty;
        var includePrereleases = ReadEnvironmentFlag(GitHubPrereleaseVariable) ?? IsPrereleaseChannel(channel);
        var source = new GithubSource(RepositoryUrl, accessToken, includePrereleases);

        return new UpdateManager(source, options, logger, locator);
    }

    private static string? ReadEnvironmentValue(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool? ReadEnvironmentFlag(string name)
    {
        var value = ReadEnvironmentValue(name);
        if (value is null)
        {
            return null;
        }

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrereleaseChannel(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            return false;
        }

        return !channel.Equals("win", StringComparison.OrdinalIgnoreCase)
            && !channel.Equals("stable", StringComparison.OrdinalIgnoreCase)
            && !channel.Equals("release", StringComparison.OrdinalIgnoreCase)
            && !channel.Equals("prod", StringComparison.OrdinalIgnoreCase)
            && !channel.Equals("production", StringComparison.OrdinalIgnoreCase);
    }

    private string? CurrentVersionText()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return _manager.CurrentVersion?.ToString()
            ?? assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();
    }
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
