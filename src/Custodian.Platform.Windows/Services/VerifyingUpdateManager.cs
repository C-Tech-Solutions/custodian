using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Custodian.Platform.Windows.Services;

/// <summary>
/// An <see cref="UpdateManager"/> that verifies the full update package before Velopack uses it.
/// </summary>
/// <remarks>
/// Velopack's own download extracts the package's Update.exe over the installed one as soon as the
/// download ends, before Custodian could check any signature. It also opens the package before
/// Custodian's size limits apply. This class downloads the full package first, verifies it, and
/// puts it where Velopack looks for it. Velopack then finds a package that passes its checksum,
/// skips its own download, and extracts Update.exe from the verified file.
/// Deltas are not used: releases ship full packages only, and a delta rebuild would create a new
/// package from the local base package without the same check.
/// </remarks>
internal sealed class VerifyingUpdateManager : UpdateManager
{
    private readonly IUpdatePackageVerifier _packageVerifier;

    public VerifyingUpdateManager(
        IUpdateSource source,
        UpdateOptions? options,
        ILogger? logger,
        IVelopackLocator? locator,
        IUpdatePackageVerifier packageVerifier)
        : base(source, options, logger, locator)
    {
        _packageVerifier = packageVerifier ?? throw new ArgumentNullException(nameof(packageVerifier));
    }

    public override async Task DownloadUpdatesAsync(
        UpdateInfo updates,
        Action<int>? progress = null,
        bool ignoreDeltas = false,
        CancellationToken cancelToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var target = updates.TargetFullRelease
            ?? throw new ArgumentException("The update has no full release to download.", nameof(updates));
        progress ??= _ => { };
        EnsureInstalled();

        await VerifiedUpdatePackageStager.StageAsync(
            target,
            Locator.PackagesDir,
            (path, cancellationToken) => Source.DownloadReleaseEntry(Log, target, path, progress, cancellationToken),
            path =>
            {
                // Velopack's own checksum is what lets its download below reuse this file.
                VerifyPackageChecksum(target, path);
                _packageVerifier.VerifyFile(target, path);
            },
            Log,
            cancelToken).ConfigureAwait(false);

        await base.DownloadUpdatesAsync(updates, progress, ignoreDeltas: true, cancelToken).ConfigureAwait(false);

        // When cancellation arrives after the package is in place, Velopack returns normally but
        // skips extracting Update.exe. Report that as cancelled, not as a finished download.
        cancelToken.ThrowIfCancellationRequested();
        progress(100);
    }
}

/// <summary>
/// Puts a verified copy of an update package in the packages folder. The package is downloaded to
/// a temporary file next to its final path and moved into place only after it passes verification,
/// so nothing unverified ever sits at the path Velopack uses.
/// </summary>
/// <remarks>
/// Verification hashes the package and checks every executable's signature online, so it runs on
/// the thread pool. Callers await this from the UI thread, and a package already on disk, or one a
/// local source copies synchronously, would otherwise be checked there.
/// </remarks>
internal static class VerifiedUpdatePackageStager
{
    public static async Task<string> StageAsync(
        VelopackAsset asset,
        string? packagesDirectory,
        Func<string, CancellationToken, Task> download,
        Action<string> verify,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(verify);
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrWhiteSpace(packagesDirectory))
        {
            throw new InvalidOperationException("Custodian could not locate the Velopack packages directory.");
        }

        UpdatePackageFileNamePolicy.EnsureSafe(asset.FileName);
        var packagePath = Path.Combine(packagesDirectory, asset.FileName);

        if (File.Exists(packagePath))
        {
            try
            {
                await Task.Run(() => verify(packagePath), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                logger.LogInformation("Update package {Package} is already downloaded and verified.", asset.FileName);
                return packagePath;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Existing update package {Package} failed verification. Downloading it again.", asset.FileName);
                File.Delete(packagePath);
            }
        }

        Directory.CreateDirectory(packagesDirectory);

        // The .partial suffix matches the files Velopack removes when it cleans up the packages
        // folder, so a copy left by a crash does not stay behind.
        var stagingPath = Path.Combine(packagesDirectory, $"{asset.FileName}.{Guid.NewGuid():N}.partial");
        try
        {
            await download(stagingPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => verify(stagingPath), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stagingPath, packagePath, overwrite: true);
            logger.LogInformation("Downloaded and verified update package {Package}.", asset.FileName);
            return packagePath;
        }
        finally
        {
            TryDeleteFile(stagingPath, logger);
        }
    }

    private static void TryDeleteFile(string path, ILogger logger)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete the temporary update package {Path}.", path);
        }
    }
}
