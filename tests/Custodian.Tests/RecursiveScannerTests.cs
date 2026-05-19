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
}
