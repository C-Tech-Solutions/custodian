using System.Diagnostics;
using Custodian.Core.Analysis;
using Custodian.Core.Export;
using Custodian.Core.Model;
using Custodian.Core.Scanning;
using Custodian.Core.Storage;

namespace Custodian.Tests;

public sealed class RecursiveScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CustodianTests", Guid.NewGuid().ToString("N"));

    public RecursiveScannerTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task RecursiveScanAggregatesNestedFoldersAndFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "root.bin"), new byte[10]);
        await File.WriteAllBytesAsync(Path.Combine(_root, "alpha", "child.log"), new byte[15]);

        var result = await new DiskScanner().ScanAsync(new ScanOptions(_root, ScanMode.Recursive));

        Assert.Equal("Recursive", result.Engine);
        Assert.Equal(25, result.Root.LogicalSizeBytes);
        Assert.Equal(2, result.Root.FileCount);
        Assert.Equal(1, result.Root.DirectoryCount);
        Assert.Empty(result.SkippedEntries);
        Assert.NotNull(result.GlobalIndex);
        Assert.Equal("child.log", result.GlobalIndex.LargestFiles[0].Name);
        Assert.Single(result.GlobalIndex.LargestFolders);
    }

    [Fact]
    public async Task RecursiveScanReturnsBeforeTraversalCompletes()
    {
        var fileSystem = new TestRecursiveScanFileSystem
        {
            DirectoryEnumerationDelay = TimeSpan.FromMilliseconds(750)
        };
        var provider = new RecursiveScanProvider(fileSystem);

        var watch = Stopwatch.StartNew();
        var scanTask = provider.ScanAsync(new ScanOptions(_root, ScanMode.Recursive), null, CancellationToken.None);
        var returnedAfter = watch.Elapsed;

        Assert.True(returnedAfter < TimeSpan.FromMilliseconds(250), $"ScanAsync returned after {returnedAfter.TotalMilliseconds:n0} ms.");
        await scanTask;
    }

    [Fact]
    public async Task RecursiveScanHonorsCancellation()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "note.txt"), "hello");
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress<ScanProgress>(_ => cts.Cancel());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new DiskScanner().ScanAsync(new ScanOptions(_root, ScanMode.Recursive), progress, cts.Token));
    }

    [Fact]
    public async Task ScannerReportsReparsePointsAsSkippedByDefault()
    {
        var target = Path.Combine(_root, "target");
        var link = Path.Combine(_root, "link");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "data.txt"), "data");

        if (!TryCreateDirectoryJunction(link, target))
        {
            return;
        }

        var result = await new DiskScanner().ScanAsync(new ScanOptions(_root, ScanMode.Recursive));

        Assert.Contains(result.SkippedEntries, e => e.Path.EndsWith("link", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScannerAvoidsAllocatedSizeProbeForCloudPlaceholders()
    {
        var cloudFile = Path.Combine(_root, "cloud-placeholder.bin");
        await File.WriteAllBytesAsync(cloudFile, new byte[42]);
        var fileSystem = new TestRecursiveScanFileSystem
        {
            ThrowOnAllocatedSize = true
        };
        fileSystem.AttributeOverrides[cloudFile] =
            FileAttributes.Archive |
            FileAttributes.Offline |
            (FileAttributes)0x00400000;
        var provider = new RecursiveScanProvider(fileSystem);

        var result = await provider.ScanAsync(
            new ScanOptions(_root, ScanMode.Recursive, CollectAllocatedSize: true),
            null,
            CancellationToken.None);

        var entry = Assert.Single(result.Root.Children);
        Assert.Equal(cloudFile, entry.FullPath);
        Assert.Equal(42, entry.LogicalSizeBytes);
        Assert.Equal(42, entry.AllocatedSizeBytes);
        Assert.Equal(0, fileSystem.AllocatedSizeCalls);
        Assert.Empty(result.SkippedEntries);
    }

    [Theory]
    [InlineData((int)FileAttributes.Offline)]
    [InlineData(0x00040000)]
    [InlineData(0x00400000)]
    public async Task ScannerAvoidsAllocatedSizeProbeForHydrationProneAttributes(int attributeValue)
    {
        var cloudFile = Path.Combine(_root, $"cloud-{attributeValue:x}.bin");
        await File.WriteAllBytesAsync(cloudFile, new byte[42]);
        var fileSystem = new TestRecursiveScanFileSystem
        {
            ThrowOnAllocatedSize = true
        };
        fileSystem.AttributeOverrides[cloudFile] = FileAttributes.Archive | (FileAttributes)attributeValue;
        var provider = new RecursiveScanProvider(fileSystem);

        var result = await provider.ScanAsync(
            new ScanOptions(_root, ScanMode.Recursive, CollectAllocatedSize: true),
            null,
            CancellationToken.None);

        var entry = Assert.Single(result.Root.Children);
        Assert.Equal(cloudFile, entry.FullPath);
        Assert.Equal(42, entry.AllocatedSizeBytes);
        Assert.Equal(0, fileSystem.AllocatedSizeCalls);
    }

    [Theory]
    [InlineData("nextcloud", "Nextcloud", "cloud.example.test")]
    [InlineData("dropbox", "Dropbox", "Personal")]
    public async Task CloudProviderScanStampsMetadataAndForcesRecursiveMode(
        string providerId,
        string providerName,
        string accountLabel)
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "cloud-file.bin"), new byte[10]);
        var metadata = new CloudProviderMetadata(providerId, providerName, accountLabel, _root);

        var result = await new DiskScanner().ScanAsync(
            new ScanOptions(_root, ScanMode.Mft, CollectAllocatedSize: true, CloudProvider: metadata));

        Assert.Equal("Recursive", result.Engine);
        Assert.Equal(ScanSourceKind.FileSystem, result.SourceKind);
        Assert.Equal(metadata, result.CloudProvider);
        Assert.Equal(10, result.Root.LogicalSizeBytes);
    }

    [Fact]
    public async Task DropboxProviderScanForcesRecursiveModeFromAuto()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "dropbox-file.bin"), new byte[10]);
        var metadata = new CloudProviderMetadata("dropbox", "Dropbox", "Personal", _root);

        var result = await new DiskScanner().ScanAsync(
            new ScanOptions(_root, ScanMode.Auto, CollectAllocatedSize: true, CloudProvider: metadata));

        Assert.Equal("Recursive", result.Engine);
        Assert.Equal(metadata, result.CloudProvider);
        Assert.Equal(10, result.Root.LogicalSizeBytes);
    }

    [Fact]
    public async Task CloudProviderScanTraversesCloudPlaceholderReparseDirectories()
    {
        var cloudDirectory = Path.Combine(_root, "cloud-folder");
        Directory.CreateDirectory(cloudDirectory);
        await File.WriteAllBytesAsync(Path.Combine(cloudDirectory, "placeholder-child.bin"), new byte[10]);
        var fileSystem = new TestRecursiveScanFileSystem();
        fileSystem.AttributeOverrides[cloudDirectory] =
            FileAttributes.Directory |
            FileAttributes.ReparsePoint |
            FileAttributes.Offline |
            (FileAttributes)0x00400000;
        fileSystem.CloudFilesReparsePoints.Add(cloudDirectory);
        var provider = new RecursiveScanProvider(fileSystem);
        var metadata = new CloudProviderMetadata("onedrive", "OneDrive", "Personal", _root);

        var result = await provider.ScanAsync(
            new ScanOptions(_root, ScanMode.Recursive, CloudProvider: metadata),
            null,
            CancellationToken.None);

        Assert.Equal(10, result.Root.LogicalSizeBytes);
        Assert.Contains(result.Root.Children, entry => entry.FullPath == cloudDirectory);
        Assert.DoesNotContain(result.SkippedEntries, entry => entry.Path == cloudDirectory);
    }

    [Fact]
    public async Task CloudProviderScanStillSkipsNonCloudReparseDirectories()
    {
        var junctionLikeDirectory = Path.Combine(_root, "junction-like");
        Directory.CreateDirectory(junctionLikeDirectory);
        await File.WriteAllBytesAsync(Path.Combine(junctionLikeDirectory, "loop-risk.bin"), new byte[10]);
        var fileSystem = new TestRecursiveScanFileSystem();
        fileSystem.AttributeOverrides[junctionLikeDirectory] =
            FileAttributes.Directory |
            FileAttributes.ReparsePoint |
            FileAttributes.Offline |
            (FileAttributes)0x00400000;
        var provider = new RecursiveScanProvider(fileSystem);
        var metadata = new CloudProviderMetadata("onedrive", "OneDrive", "Personal", _root);

        var result = await provider.ScanAsync(
            new ScanOptions(_root, ScanMode.Recursive, CloudProvider: metadata),
            null,
            CancellationToken.None);

        Assert.Equal(0, result.Root.LogicalSizeBytes);
        Assert.Contains(result.SkippedEntries, entry =>
            entry.Path == junctionLikeDirectory &&
            entry.Reason.Contains("reparse", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CloudProviderScanSkipsHydrationProneNonReparseDirectories()
    {
        var offlineDirectory = Path.Combine(_root, "offline-folder");
        Directory.CreateDirectory(offlineDirectory);
        await File.WriteAllBytesAsync(Path.Combine(offlineDirectory, "online-only-child.bin"), new byte[10]);
        var fileSystem = new TestRecursiveScanFileSystem();
        fileSystem.AttributeOverrides[offlineDirectory] =
            FileAttributes.Directory |
            FileAttributes.Offline |
            (FileAttributes)0x00400000;
        var provider = new RecursiveScanProvider(fileSystem);
        var metadata = new CloudProviderMetadata("onedrive", "OneDrive", "Personal", _root);

        var result = await provider.ScanAsync(
            new ScanOptions(_root, ScanMode.Recursive, CloudProvider: metadata),
            null,
            CancellationToken.None);

        Assert.Equal(0, result.Root.LogicalSizeBytes);
        Assert.Contains(result.SkippedEntries, entry =>
            entry.Path == offlineDirectory &&
            entry.Reason.Contains("cloud placeholder", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScannerUsesAllocatedSizeProbeForUnpinnedLocalFiles()
    {
        var unpinnedFile = Path.Combine(_root, "unpinned-local.bin");
        await File.WriteAllBytesAsync(unpinnedFile, new byte[42]);
        var fileSystem = new TestRecursiveScanFileSystem();
        fileSystem.AttributeOverrides[unpinnedFile] =
            FileAttributes.Archive |
            (FileAttributes)0x00100000;
        fileSystem.AllocatedSizeOverrides[unpinnedFile] = 7;
        var provider = new RecursiveScanProvider(fileSystem);

        var result = await provider.ScanAsync(
            new ScanOptions(_root, ScanMode.Recursive, CollectAllocatedSize: true),
            null,
            CancellationToken.None);

        var entry = Assert.Single(result.Root.Children);
        Assert.Equal(unpinnedFile, entry.FullPath);
        Assert.Equal(42, entry.LogicalSizeBytes);
        Assert.Equal(7, entry.AllocatedSizeBytes);
        Assert.Equal(1, fileSystem.AllocatedSizeCalls);
        Assert.Empty(result.SkippedEntries);
    }

    [Fact]
    public async Task ScannerKeepsEntriesListedBeforeListingFailsAndReportsDirectoryOnce()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(_root, "b.txt"), "bb");
        await File.WriteAllTextAsync(Path.Combine(_root, "c.txt"), "ccc");
        var fileSystem = new TestRecursiveScanFileSystem();
        fileSystem.ListingFailures[_root] = (2, new IOException("listing interrupted"));
        var provider = new RecursiveScanProvider(fileSystem);

        var result = await provider.ScanAsync(new ScanOptions(_root, ScanMode.Recursive), null, CancellationToken.None);

        Assert.Equal(2, result.Root.FileCount);
        Assert.Equal(2, result.Root.Children.Count);
        var skipped = Assert.Single(result.SkippedEntries);
        Assert.Equal(_root, skipped.Path);
        Assert.Contains("listing interrupted", skipped.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnreadableDirectoryIsReportedOnce()
    {
        var locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(locked);
        await File.WriteAllTextAsync(Path.Combine(_root, "visible.txt"), "hello");
        var fileSystem = new TestRecursiveScanFileSystem();
        fileSystem.ListingFailures[locked] = (0, new UnauthorizedAccessException("access denied"));
        var provider = new RecursiveScanProvider(fileSystem);

        var result = await provider.ScanAsync(new ScanOptions(_root, ScanMode.Recursive), null, CancellationToken.None);

        var skipped = Assert.Single(result.SkippedEntries);
        Assert.Equal(locked, skipped.Path);
        Assert.Equal("access denied", skipped.Reason);
        Assert.Contains(result.Root.Children, entry => entry.FullPath == locked);
        Assert.Equal(1, result.Root.FileCount);
        Assert.Equal(1, result.Root.DirectoryCount);
    }

    [Fact]
    public async Task ScanRecordsEntryMetadataFromTheDirectoryListing()
    {
        var folder = Path.Combine(_root, "folder");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Report.TXT");
        await File.WriteAllBytesAsync(file, new byte[123]);
        var lastWrite = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, lastWrite);

        var result = await new DiskScanner().ScanAsync(new ScanOptions(_root, ScanMode.Recursive));

        var folderEntry = Assert.Single(result.Root.Children);
        Assert.True(folderEntry.IsDirectory);
        Assert.Equal("folder", folderEntry.Name);
        Assert.Equal(new DirectoryInfo(folder).Attributes.ToString(), folderEntry.Attributes);
        var fileEntry = Assert.Single(folderEntry.Children);
        Assert.Equal("Report.TXT", fileEntry.Name);
        Assert.Equal(file, fileEntry.FullPath);
        Assert.Equal(".txt", fileEntry.Extension);
        Assert.Equal(123, fileEntry.LogicalSizeBytes);
        Assert.Equal(new FileInfo(file).Attributes.ToString(), fileEntry.Attributes);
        Assert.Equal(new DateTimeOffset(lastWrite), fileEntry.LastWriteTime);
        Assert.Equal(TimeSpan.Zero, fileEntry.LastWriteTime!.Value.Offset);
        Assert.Equal(new DirectoryInfo(_root).LastWriteTimeUtc, result.Root.LastWriteTime!.Value.UtcDateTime);
    }

    [Fact]
    public async Task ScanListsDirectoriesWhoseNamesEndInDotOrSpace()
    {
        var odd = Path.Combine(_root, "trailing. ");
        var extendedOdd = @"\\?\" + odd;
        Directory.CreateDirectory(extendedOdd);
        await File.WriteAllBytesAsync(Path.Combine(extendedOdd, "inside.bin"), new byte[7]);
        try
        {
            var result = await new DiskScanner().ScanAsync(new ScanOptions(_root, ScanMode.Recursive));

            Assert.Empty(result.SkippedEntries);
            var oddEntry = Assert.Single(result.Root.Children);
            Assert.Equal(odd, oddEntry.FullPath);
            Assert.Equal(7, result.Root.LogicalSizeBytes);
            Assert.Equal(1, result.Root.FileCount);
        }
        finally
        {
            Directory.Delete(extendedOdd, recursive: true);
        }
    }

    [Fact]
    public void ExtensionMatchesFileSystemInfoExtension()
    {
        string[] names = ["archive.tar.gz", "README", ".gitignore", "Mixed.CaSe", "trailing."];
        foreach (var name in names)
        {
            File.WriteAllText(@"\\?\" + Path.Combine(_root, name), "x");
        }

        var files = new DirectoryInfo(_root).EnumerateFiles().ToList();

        Assert.Equal(names.Length, files.Count);
        foreach (var file in files)
        {
            Assert.Equal(file.Extension, RecursiveScanProvider.GetExtension(file.Name));
        }

        foreach (var name in names)
        {
            File.Delete(@"\\?\" + Path.Combine(_root, name));
        }
    }

    [Theory]
    [InlineData(@"C:\Data\Folder", @"C:\Data\Folder")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\Data\Folder.", @"\\?\C:\Data\Folder.")]
    [InlineData(@"C:\Data\Folder \Child", @"\\?\C:\Data\Folder \Child")]
    [InlineData(@"\\server\share\Folder.", @"\\?\UNC\server\share\Folder.")]
    [InlineData(@"\\?\C:\Data\Folder.", @"\\?\C:\Data\Folder.")]
    public void ListingPathKeepsTrailingDotsAndSpaces(string path, string expected)
    {
        Assert.Equal(expected, RecursiveScanFileSystem.ToListingPath(path));
    }

    [Fact]
    public async Task OneAndEightThreadsProduceIdenticalResults()
    {
        var single = await ScanFakeTreeAsync(1);
        var parallel = await ScanFakeTreeAsync(8);

        Assert.Equal(FakeTree.ExpectedFiles, single.Root.FileCount);
        Assert.Equal(FakeTree.ExpectedFolders, single.Root.DirectoryCount);
        Assert.Equal(FakeTree.ExpectedBytes, single.Root.LogicalSizeBytes);
        Assert.True(single.Root.DirectoryCount > 2_000, $"Fake tree has {single.Root.DirectoryCount} folders.");
        Assert.Equal(Describe(single.Root), Describe(parallel.Root));
        Assert.Equal(single.SkippedEntries, parallel.SkippedEntries);
        Assert.Contains(single.SkippedEntries, entry => entry.Reason == "Skipped reparse point");
        Assert.Contains(single.SkippedEntries, entry => entry.Reason.Contains("is denied", StringComparison.Ordinal));
        Assert.Contains(single.SkippedEntries, entry => entry.Reason.StartsWith("Allocated size unavailable", StringComparison.Ordinal));
        Assert.Equal(
            single.GlobalIndex!.LargestFolders.Select(entry => entry.FullPath),
            parallel.GlobalIndex!.LargestFolders.Select(entry => entry.FullPath));
        Assert.Equal(
            single.GlobalIndex.LargestFiles.Select(entry => entry.FullPath),
            parallel.GlobalIndex.LargestFiles.Select(entry => entry.FullPath));
        Assert.Equal(single.GlobalIndex.ExtensionSummaries, parallel.GlobalIndex.ExtensionSummaries);
        Assert.Contains("Recursive scan threads: 1", single.Diagnostics);
        Assert.Contains("Recursive scan threads: 8", parallel.Diagnostics);
    }

    [Fact]
    public async Task ParallelScanMatchesSingleThreadedScanOnRealFolders()
    {
        for (var a = 0; a < 6; a++)
        {
            for (var b = 0; b < 4; b++)
            {
                var folder = Path.Combine(_root, $"a{a}", $"b{b}");
                Directory.CreateDirectory(folder);
                for (var f = 0; f < a + b; f++)
                {
                    await File.WriteAllBytesAsync(Path.Combine(folder, $"f{f}.dat"), new byte[(a * 31) + (b * 7) + f]);
                }
            }
        }

        var single = await new DiskScanner().ScanAsync(new ScanOptions(_root, ScanMode.Recursive, MaxDegreeOfParallelism: 1));
        var parallel = await new DiskScanner().ScanAsync(new ScanOptions(_root, ScanMode.Recursive, MaxDegreeOfParallelism: 8));

        Assert.Equal(30, single.Root.DirectoryCount);
        // NTFS updates the copy of a folder's timestamps in its parent's index lazily, so a
        // freshly written folder can show a newer time on the second scan.
        Assert.Equal(Describe(single.Root, includeFolderTimes: false), Describe(parallel.Root, includeFolderTimes: false));
    }

    [Fact]
    public async Task WorkerFailureFailsTheScan()
    {
        var fileSystem = new FakeTree
        {
            RootPath = _root,
            FailingDirectory = Path.Combine(_root, "d3", "d4")
        };
        var provider = new RecursiveScanProvider(fileSystem);

        var scan = provider.ScanAsync(new ScanOptions(_root, ScanMode.Recursive, MaxDegreeOfParallelism: 8), null, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => scan.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(FakeTree.FailureMessage, exception.Message);
    }

    [Fact]
    public async Task CancellationStopsAllWorkers()
    {
        using var cts = new CancellationTokenSource();
        var fileSystem = new FakeTree
        {
            RootPath = _root,
            ListingDelay = TimeSpan.FromMilliseconds(2),
            OnListing = count =>
            {
                if (count == 200)
                {
                    cts.Cancel();
                }
            }
        };
        var provider = new RecursiveScanProvider(fileSystem);

        var scan = provider.ScanAsync(new ScanOptions(_root, ScanMode.Recursive, MaxDegreeOfParallelism: 8), null, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(TimeSpan.FromSeconds(30)));
        var listingsAtCompletion = fileSystem.ListingCount;
        await Task.Delay(200);
        Assert.Equal(listingsAtCompletion, fileSystem.ListingCount);
        Assert.True(listingsAtCompletion < FakeTree.ExpectedFolders, $"Listed {listingsAtCompletion} directories after cancellation.");
    }

    [Theory]
    [InlineData(null, false, 0)]
    [InlineData(3, false, 3)]
    [InlineData(0, false, 1)]
    [InlineData(500, false, 64)]
    [InlineData(8, true, 1)]
    public void DegreeOfParallelismFollowsOptions(int? requested, bool cloud, int expected)
    {
        var options = new ScanOptions(
            _root,
            ScanMode.Recursive,
            CloudProvider: cloud ? new CloudProviderMetadata("onedrive", "OneDrive", "Personal", _root) : null,
            MaxDegreeOfParallelism: requested);

        var degree = RecursiveScanProvider.ResolveDegreeOfParallelism(options);

        Assert.Equal(expected == 0 ? Math.Clamp(Environment.ProcessorCount, 1, 8) : expected, degree);
    }

    [Fact]
    public async Task AnalysisBuildsLargestFilesAndExtensionSummary()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "a.txt"), new byte[10]);
        await File.WriteAllBytesAsync(Path.Combine(_root, "b.bin"), new byte[20]);
        await File.WriteAllBytesAsync(Path.Combine(_root, "c.bin"), new byte[30]);

        var result = await new DiskScanner().ScanAsync(new ScanOptions(_root, ScanMode.Recursive));
        var largest = ScanAnalysis.LargestFiles(result, 1);
        var extensions = ScanAnalysis.ExtensionSummary(result);

        Assert.Equal("c.bin", largest.Single().Name);
        Assert.Equal(50, extensions.Single(e => e.Extension == ".bin").LogicalSizeBytes);
        Assert.Equal(2, extensions.Single(e => e.Extension == ".bin").FileCount);
    }

    [Fact]
    public async Task StoreRoundTripsCustodianScanSqliteFile()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        await File.WriteAllTextAsync(Path.Combine(_root, "alpha", "note.txt"), "hello");
        var scanPath = Path.Combine(_root, "scan.custodian-scan");
        var store = new ScanStore();
        var result = await new DiskScanner().ScanAsync(new ScanOptions(_root, ScanMode.Recursive));

        await store.SaveAsync(result, scanPath);
        var loaded = await store.LoadAsync(scanPath);

        Assert.Equal(result.RootPath, loaded.RootPath);
        Assert.Equal(result.Root.LogicalSizeBytes, loaded.Root.LogicalSizeBytes);
        Assert.Equal(result.Root.FileCount, loaded.Root.FileCount);
        Assert.NotNull(loaded.GlobalIndex);
        Assert.Equal("note.txt", loaded.GlobalIndex.LargestFiles.Single().Name);
        Assert.Equal("custodian-scan", Path.GetExtension(scanPath).TrimStart('.'));
    }

    [Fact]
    public async Task CsvAndJsonExportsIncludeScannedEntries()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "note.txt"), "hello");
        var csvPath = Path.Combine(_root, "scan.csv");
        var jsonPath = Path.Combine(_root, "scan.json");
        var result = await new DiskScanner().ScanAsync(new ScanOptions(_root, ScanMode.Recursive));

        await ScanExporter.ExportCsvAsync(result, csvPath);
        await ScanExporter.ExportJsonAsync(result, jsonPath);

        Assert.Contains("note.txt", await File.ReadAllTextAsync(csvPath));
        Assert.Contains("note.txt", await File.ReadAllTextAsync(jsonPath));
    }

    [Fact]
    public void MftProviderRejectsNetworkSharesWithoutScanning()
    {
        var provider = new MftScanProvider();

        var canScan = provider.CanScan(new ScanOptions(@"\\server\share", ScanMode.Mft), out var reason);

        Assert.False(canScan);
        Assert.Contains("UNC", reason);
    }

    [Fact]
    public async Task AutoModeUsesRecursiveScannerForSubfolders()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "note.txt"), "hello");

        var result = await new DiskScanner().ScanAsync(new ScanOptions(_root, ScanMode.Auto));

        Assert.Equal("Recursive", result.Engine);
    }

    [Fact]
    public void BareDrivePathNormalizesToDriveRoot()
    {
        Assert.Equal(@"C:\", ScanPathUtility.NormalizeRoot("C:"));
        Assert.True(ScanPathUtility.IsVolumeRoot("C:"));
    }

    private async Task<ScanResult> ScanFakeTreeAsync(int threads)
    {
        var provider = new RecursiveScanProvider(new FakeTree { RootPath = _root });
        return await provider.ScanAsync(
            new ScanOptions(_root, ScanMode.Recursive, CollectAllocatedSize: true, MaxDegreeOfParallelism: threads),
            null,
            CancellationToken.None);
    }

    private static List<string> Describe(FileSystemEntry root, bool includeFolderTimes = true)
        => root.Flatten()
            .Select(entry => string.Join(
                '|',
                entry.FullPath,
                entry.Name,
                entry.IsDirectory,
                entry.LogicalSizeBytes,
                entry.AllocatedSizeBytes,
                entry.FileCount,
                entry.DirectoryCount,
                entry.Extension,
                entry.Attributes,
                entry.IsDirectory && !includeFolderTimes ? null : entry.LastWriteTime?.ToString("O"),
                entry.Children.Count))
            .ToList();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool TryCreateDirectoryJunction(string linkPath, string targetPath)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit();
            return process?.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch
        {
            return false;
        }
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
            => handler(value);
    }

    /// <summary>Real directories, with attribute overrides and injected listing failures.</summary>
    private sealed class TestRecursiveScanFileSystem : IRecursiveScanFileSystem
    {
        private int _allocatedSizeCalls;

        public Dictionary<string, FileAttributes> AttributeOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Directory path to the number of entries returned before the listing throws.</summary>
        public Dictionary<string, (int AfterEntries, Exception Exception)> ListingFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, long> AllocatedSizeOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> CloudFilesReparsePoints { get; } = new(StringComparer.OrdinalIgnoreCase);

        public TimeSpan DirectoryEnumerationDelay { get; set; }

        public bool ThrowOnAllocatedSize { get; set; }

        public int AllocatedSizeCalls => Volatile.Read(ref _allocatedSizeCalls);

        public bool DirectoryExists(string path)
            => Directory.Exists(path);

        public IEnumerable<ScanItem> EnumerateEntries(string directoryPath)
        {
            if (DirectoryEnumerationDelay > TimeSpan.Zero)
            {
                Thread.Sleep(DirectoryEnumerationDelay);
            }

            if (ListingFailures.TryGetValue(directoryPath, out var failure) && failure.AfterEntries == 0)
            {
                throw failure.Exception;
            }

            return Enumerate(directoryPath, failure.Exception is null ? null : failure);
        }

        public bool IsCloudFilesReparsePoint(string directoryPath)
            => CloudFilesReparsePoints.Contains(directoryPath);

        private IEnumerable<ScanItem> Enumerate(string directoryPath, (int AfterEntries, Exception Exception)? failure)
        {
            var returned = 0;
            foreach (var item in RecursiveScanFileSystem.Instance.EnumerateEntries(directoryPath))
            {
                yield return AttributeOverrides.TryGetValue(Path.Join(directoryPath, item.Name), out var attributes)
                    ? item with { Attributes = attributes }
                    : item;

                if (failure is { } planned && ++returned == planned.AfterEntries)
                {
                    throw planned.Exception;
                }
            }
        }

        public long GetAllocatedSize(string path, long fallbackLength)
        {
            Interlocked.Increment(ref _allocatedSizeCalls);
            if (ThrowOnAllocatedSize)
            {
                throw new IOException("Allocated size probe should not run.");
            }

            if (AllocatedSizeOverrides.TryGetValue(path, out var allocatedSize))
            {
                return allocatedSize;
            }

            return fallbackLength;
        }
    }

    /// <summary>
    /// In-memory tree under the test root: three levels of 15 folders with a few files each,
    /// plus reparse points, unreadable folders and allocated-size failures.
    /// </summary>
    private sealed class FakeTree : IRecursiveScanFileSystem
    {
        public const string FailureMessage = "simulated worker failure";
        private const int FanOut = 15;
        private const int Depth = 3;
        private static readonly DateTimeOffset BaseTime = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private int _listingCount;

        static FakeTree()
        {
            long folders = 0, files = 0, bytes = 0;
            Count([], ref folders, ref files, ref bytes);
            ExpectedFolders = folders;
            ExpectedFiles = files;
            ExpectedBytes = bytes;
        }

        public static long ExpectedFolders { get; }

        public static long ExpectedFiles { get; }

        public static long ExpectedBytes { get; }

        public required string RootPath { get; init; }

        public TimeSpan ListingDelay { get; init; }

        public Action<int>? OnListing { get; init; }

        public string? FailingDirectory { get; init; }

        public int ListingCount => Volatile.Read(ref _listingCount);

        public bool DirectoryExists(string path)
            => Directory.Exists(path);

        public IEnumerable<ScanItem> EnumerateEntries(string directoryPath)
        {
            var count = Interlocked.Increment(ref _listingCount);
            OnListing?.Invoke(count);
            if (ListingDelay > TimeSpan.Zero)
            {
                Thread.Sleep(ListingDelay);
            }

            if (string.Equals(directoryPath, FailingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(FailureMessage);
            }

            var indices = ParseIndices(directoryPath);
            if (IsUnreadable(indices))
            {
                throw new UnauthorizedAccessException($"Access to the path '{directoryPath}' is denied.");
            }

            return ListChildren(indices);
        }

        public bool IsCloudFilesReparsePoint(string directoryPath)
            => false;

        public long GetAllocatedSize(string path, long fallbackLength)
        {
            if (path.EndsWith(@"\f0.bin", StringComparison.Ordinal) && path.Contains(@"\d7\", StringComparison.Ordinal))
            {
                throw new IOException("allocated size unavailable");
            }

            return (fallbackLength + 4095) / 4096 * 4096;
        }

        private static IEnumerable<ScanItem> ListChildren(int[] indices)
        {
            if (indices.Length < Depth)
            {
                for (var i = 0; i < FanOut; i++)
                {
                    int[] child = [.. indices, i];
                    var attributes = IsReparsePoint(child)
                        ? FileAttributes.Directory | FileAttributes.ReparsePoint
                        : FileAttributes.Directory;
                    yield return new ScanItem($"d{i}", attributes, 0, BaseTime.AddMinutes(Hash(child)));
                }
            }

            var fileCount = FileCount(indices);
            for (var f = 0; f < fileCount; f++)
            {
                var attributes = f % 3 == 0 ? FileAttributes.Archive : FileAttributes.Archive | FileAttributes.Hidden;
                var extension = (f % 4) switch { 0 => ".bin", 1 => ".TXT", 2 => string.Empty, _ => ".log" };
                yield return new ScanItem($"f{f}{extension}", attributes, FileLength(indices, f), BaseTime.AddSeconds(Hash(indices) + f));
            }
        }

        private static void Count(int[] indices, ref long folders, ref long files, ref long bytes)
        {
            if (indices.Length > 0)
            {
                folders++;
            }

            if (IsUnreadable(indices))
            {
                return;
            }

            var fileCount = FileCount(indices);
            files += fileCount;
            for (var f = 0; f < fileCount; f++)
            {
                bytes += FileLength(indices, f);
            }

            if (indices.Length == Depth)
            {
                return;
            }

            for (var i = 0; i < FanOut; i++)
            {
                int[] child = [.. indices, i];
                if (!IsReparsePoint(child))
                {
                    Count(child, ref folders, ref files, ref bytes);
                }
            }
        }

        private static int FileCount(int[] indices) => Hash(indices) % 6;

        private static long FileLength(int[] indices, int file) => ((Hash(indices) * 37L) + (file * 1009L)) % 50_000;

        private static bool IsReparsePoint(int[] indices) => indices.Length == 2 && Hash(indices) % 11 == 0;

        private static bool IsUnreadable(int[] indices) => indices.Length == 3 && Hash(indices) % 13 == 0;

        private static int Hash(int[] indices)
        {
            var hash = 17;
            foreach (var index in indices)
            {
                hash = unchecked((hash * 31) + index + 1);
            }

            return Math.Abs(hash % 100_000);
        }

        private int[] ParseIndices(string directoryPath)
        {
            var relative = Path.GetRelativePath(RootPath, directoryPath);
            return relative == "."
                ? []
                : relative.Split(Path.DirectorySeparatorChar).Select(part => int.Parse(part[1..])).ToArray();
        }
    }
}
