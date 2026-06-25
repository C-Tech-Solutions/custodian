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

    [Fact]
    public async Task CloudProviderScanStampsMetadataAndForcesRecursiveMode()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "onedrive-file.bin"), new byte[10]);
        var metadata = new CloudProviderMetadata("onedrive", "OneDrive", "Personal", _root);

        var result = await new DiskScanner().ScanAsync(
            new ScanOptions(_root, ScanMode.Mft, CollectAllocatedSize: true, CloudProvider: metadata));

        Assert.Equal("Recursive", result.Engine);
        Assert.Equal(ScanSourceKind.FileSystem, result.SourceKind);
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
            FileAttributes.ReparsePoint;
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
    public async Task ScannerSkipsFileWhenMetadataCannotBeRead()
    {
        var readableFile = Path.Combine(_root, "readable.txt");
        var brokenFile = Path.Combine(_root, "metadata-fails.txt");
        await File.WriteAllTextAsync(readableFile, "hello");
        await File.WriteAllTextAsync(brokenFile, "hidden");
        var fileSystem = new TestRecursiveScanFileSystem();
        fileSystem.LengthFailures[brokenFile] = new IOException("metadata unavailable");
        var provider = new RecursiveScanProvider(fileSystem);

        var result = await provider.ScanAsync(new ScanOptions(_root, ScanMode.Recursive), null, CancellationToken.None);

        Assert.Equal(1, result.Root.FileCount);
        Assert.Contains(result.Root.Children, entry => entry.FullPath == readableFile);
        Assert.DoesNotContain(result.Root.Children, entry => entry.FullPath == brokenFile);
        Assert.Contains(result.SkippedEntries, entry =>
            entry.Path == brokenFile &&
            entry.Reason.Contains("metadata unavailable", StringComparison.OrdinalIgnoreCase));
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

    private sealed class TestRecursiveScanFileSystem : IRecursiveScanFileSystem
    {
        public Dictionary<string, FileAttributes> AttributeOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Exception> LengthFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, long> AllocatedSizeOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);

        public TimeSpan DirectoryEnumerationDelay { get; set; }

        public bool ThrowOnAllocatedSize { get; set; }

        public int AllocatedSizeCalls { get; private set; }

        public bool DirectoryExists(string path)
            => Directory.Exists(path);

        public IEnumerable<DirectoryInfo> EnumerateDirectories(DirectoryInfo directory)
        {
            if (DirectoryEnumerationDelay > TimeSpan.Zero)
            {
                Thread.Sleep(DirectoryEnumerationDelay);
            }

            return directory.EnumerateDirectories();
        }

        public IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo directory)
            => directory.EnumerateFiles();

        public FileAttributes GetAttributes(FileSystemInfo info)
            => AttributeOverrides.TryGetValue(info.FullName, out var attributes)
                ? attributes
                : info.Attributes;

        public bool IsCloudFilesReparsePoint(DirectoryInfo directory)
            => false;

        public DateTimeOffset GetLastWriteTimeUtc(FileSystemInfo info)
            => info.LastWriteTimeUtc;

        public long GetLength(FileInfo file)
        {
            if (LengthFailures.TryGetValue(file.FullName, out var exception))
            {
                throw exception;
            }

            return file.Length;
        }

        public long GetAllocatedSize(string path, long fallbackLength)
        {
            AllocatedSizeCalls++;
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
}
