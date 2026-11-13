using System.Diagnostics;
using Custodian.Core.Model;

namespace Custodian.Core.Scanning;

public sealed class MftScanProvider : IDiskScanProvider
{
    internal const string DirectSourceDiagnostic = "MFT source: direct $MFT read";
    internal const string EnumerationSourceDiagnostic = "MFT source: FSCTL_ENUM_USN_DATA";

    public string Name => "NTFS MFT";

    public bool CanScan(ScanOptions options, out string reason)
    {
        reason = string.Empty;

        if (!OperatingSystem.IsWindows())
        {
            reason = "MFT scans require Windows.";
            return false;
        }

        var normalizedRoot = ScanPathUtility.NormalizeRoot(options.RootPath);

        if (normalizedRoot.StartsWith(@"\\", StringComparison.Ordinal))
        {
            reason = "MFT scans do not apply to UNC/network shares.";
            return false;
        }

        var root = Path.GetPathRoot(normalizedRoot);
        if (string.IsNullOrWhiteSpace(root))
        {
            reason = "No drive root could be resolved.";
            return false;
        }

        try
        {
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                reason = "Drive is not ready.";
                return false;
            }

            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Drive format is {drive.DriveFormat}, not NTFS.";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            reason = ex.Message;
            return false;
        }

        return true;
    }

    public async Task<ScanResult> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!CanScan(options, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        return await Task.Run(() => Scan(options, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static ScanResult Scan(
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var rootPath = ScanPathUtility.NormalizeRoot(options.RootPath);
        using var volume = NtfsVolume.Open(Path.GetPathRoot(rootPath)!);
        return Scan(options, volume, new ProgressThrottle(progress), cancellationToken);
    }

    /// <summary>
    /// Builds the scan from a direct read of the <c>$MFT</c> when <see cref="ScanOptions.UseDirectMftRead"/> is on,
    /// and from <c>FSCTL_ENUM_USN_DATA</c> plus per-file record reads when it is off or the direct read fails.
    /// </summary>
    internal static ScanResult Scan(
        ScanOptions options,
        INtfsVolume volume,
        ProgressThrottle progress,
        CancellationToken cancellationToken,
        int directReadChunkBytes = MftDirectReader.DefaultChunkBytes)
    {
        var started = DateTimeOffset.UtcNow;
        var rootPath = ScanPathUtility.NormalizeRoot(options.RootPath);
        var diagnostics = new List<string>();
        var phases = new List<ScanPhaseTiming>();
        var skipped = new List<SkippedEntry>();
        MftDirectReadResult? direct = null;

        if (options.UseDirectMftRead)
        {
            var directWatch = Stopwatch.StartNew();
            try
            {
                direct = MftDirectReader.Read(volume, progress, cancellationToken, directReadChunkBytes);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A scan never fails because the direct read did: it falls back to the enumeration.
                directWatch.Stop();
                diagnostics.Add(
                    $"{EnumerationSourceDiagnostic}; the direct $MFT read failed after {directWatch.Elapsed.TotalSeconds:0.0} s: {ex.Message}");
                phases.Add(new ScanPhaseTiming("Direct MFT read (abandoned)", directWatch.Elapsed));
                if (ex is MftDirectReadException { Statistics: { } partial })
                {
                    diagnostics.AddRange(partial.GetDiagnostics());
                }
            }
        }
        else
        {
            diagnostics.Add($"{EnumerationSourceDiagnostic}; the direct $MFT read is turned off");
        }

        MftTreeBuildResult? build = null;
        if (direct is not null)
        {
            var sizes = new MftRecordSizeSource(options);
            try
            {
                build = MftTreeBuilder.Build(direct.Records, rootPath, options, progress, skipped, cancellationToken, sizes.Resolve);
            }
            catch (DirectoryNotFoundException ex)
            {
                // A torn or mid-update record can drop the requested folder from the direct read; the enumeration may
                // still list it. A folder that really is missing fails the enumeration with the same error.
                skipped.Clear();
                diagnostics.Add($"{EnumerationSourceDiagnostic}; the direct $MFT read failed: {ex.Message}");
                diagnostics.AddRange(direct.Statistics.GetDiagnostics());
                phases.Add(new ScanPhaseTiming("Direct MFT read (abandoned)", direct.Statistics.ReadDuration + direct.Statistics.ParseDuration));
            }

            if (build is not null)
            {
                diagnostics.Add(DirectSourceDiagnostic);
                diagnostics.AddRange(direct.Statistics.GetDiagnostics());
                diagnostics.Add(sizes.GetDiagnostic());
                phases.Add(new ScanPhaseTiming("MFT read", direct.Statistics.ReadDuration));
                phases.Add(new ScanPhaseTiming("MFT parse", direct.Statistics.ParseDuration));
                phases.Add(new ScanPhaseTiming("MFT tree construction", build.TreeConstructionDuration));
                phases.Add(new ScanPhaseTiming("File entries", build.SizeResolutionDuration));
            }
        }

        if (build is null)
        {
            var enumerationWatch = Stopwatch.StartNew();
            var records = volume.EnumerateUsnRecords(progress, cancellationToken);
            enumerationWatch.Stop();
            var sizeResolver = new NtfsRecordSizeResolver(options, volume.ReadFileRecord);
            build = MftTreeBuilder.Build(records.Values, rootPath, options, progress, skipped, cancellationToken, sizeResolver.Resolve);
            diagnostics.AddRange(sizeResolver.GetDiagnostics());
            phases.Add(new ScanPhaseTiming("MFT enumeration", enumerationWatch.Elapsed));
            phases.Add(new ScanPhaseTiming("MFT tree construction", build.TreeConstructionDuration));
            phases.Add(new ScanPhaseTiming("Size resolution", build.SizeResolutionDuration));
        }

        // Printed by both sources so a direct and an enumeration scan of the same volume compare bucket by bucket.
        diagnostics.AddRange(build.Statistics.GetDiagnostics());
        phases.Add(new ScanPhaseTiming("Folder aggregation", build.AggregationDuration));

        return new ScanResult
        {
            RootPath = build.Root.FullPath,
            SourceKind = ScanSourceKind.FileSystem,
            SourceId = build.Root.FullPath,
            DisplayRootPath = build.Root.FullPath,
            Engine = "NTFS MFT",
            StartedAt = started,
            CompletedAt = DateTimeOffset.UtcNow,
            Root = build.Root,
            GlobalIndex = build.GlobalIndex,
            SkippedEntries = skipped,
            PhaseTimings = phases,
            Diagnostics = diagnostics
        };
    }
}
