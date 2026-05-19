using Custodian.Core.Analysis;
using Custodian.Core.Model;

namespace Custodian.Tests;

public sealed class ScanAnalysisTests
{
    [Fact]
    public void LargestFilesMatchesFullSortOrder()
    {
        var result = SampleResult();

        var optimized = ScanAnalysis.LargestFiles(result, 3).Select(e => e.FullPath).ToList();
        var expected = result.Root
            .Flatten()
            .Where(e => !e.IsDirectory)
            .OrderByDescending(e => e.LogicalSizeBytes)
            .ThenBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(e => e.FullPath)
            .ToList();

        Assert.Equal(expected, optimized);
    }

    [Fact]
    public void LargestFoldersMatchesFullSortOrder()
    {
        var result = SampleResult();

        var optimized = ScanAnalysis.LargestFolders(result, 2).Select(e => e.FullPath).ToList();
        var expected = result.Root
            .Flatten()
            .Where(e => e.IsDirectory)
            .OrderByDescending(e => e.LogicalSizeBytes)
            .ThenBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .Select(e => e.FullPath)
            .ToList();

        Assert.Equal(expected, optimized);
    }

    [Fact]
    public void ExtensionSummaryAggregatesWithoutChangingResults()
    {
        var summaries = ScanAnalysis.ExtensionSummary(SampleResult());

        var bin = summaries.Single(s => s.Extension == ".bin");
        Assert.Equal(2, bin.FileCount);
        Assert.Equal(140, bin.LogicalSizeBytes);
        Assert.Equal(140, bin.AllocatedSizeBytes);
    }

    private static ScanResult SampleResult()
    {
        var root = Directory(@"C:\", 195, 4, 2);
        var alpha = Directory(@"C:\Alpha", 150, 2, 0);
        alpha.Children.Add(File(@"C:\Alpha\a.bin", 80, ".bin"));
        alpha.Children.Add(File(@"C:\Alpha\b.bin", 60, ".bin"));

        var beta = Directory(@"C:\Beta", 45, 2, 0);
        beta.Children.Add(File(@"C:\Beta\a.log", 40, ".log"));
        beta.Children.Add(File(@"C:\Beta\b.txt", 15, ".txt"));

        root.Children.Add(alpha);
        root.Children.Add(beta);

        return new ScanResult
        {
            RootPath = root.FullPath,
            Root = root,
            Engine = "Test",
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    private static FileSystemEntry Directory(string path, long size, long fileCount, long directoryCount)
    {
        return new FileSystemEntry
        {
            Name = Path.GetFileName(path.TrimEnd('\\')),
            FullPath = path,
            IsDirectory = true,
            LogicalSizeBytes = size,
            AllocatedSizeBytes = size,
            FileCount = fileCount,
            DirectoryCount = directoryCount
        };
    }

    private static FileSystemEntry File(string path, long size, string extension)
    {
        return new FileSystemEntry
        {
            Name = Path.GetFileName(path),
            FullPath = path,
            IsDirectory = false,
            LogicalSizeBytes = size,
            AllocatedSizeBytes = size,
            FileCount = 1,
            Extension = extension
        };
    }
}
