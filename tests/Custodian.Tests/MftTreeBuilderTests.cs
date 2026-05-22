using Custodian.Core.Model;
using Custodian.Core.Scanning;

namespace Custodian.Tests;

public sealed class MftTreeBuilderTests
{
    [Fact]
    public void BuildCreatesTreeFromParentReferencesWithoutDirectoryProbing()
    {
        var records = SampleRecords();
        var skipped = new List<SkippedEntry>();

        var result = MftTreeBuilder.Build(
            records,
            @"C:\",
            new ScanOptions(@"C:\", ScanMode.Mft),
            new ProgressThrottle(null),
            skipped,
            CancellationToken.None,
            FakeSizeResolver);

        Assert.Equal(@"C:\", result.Root.FullPath);
        Assert.Equal(2, result.Root.FileCount);
        Assert.Equal(2, result.Root.DirectoryCount);
        Assert.Equal(125, result.Root.LogicalSizeBytes);
        Assert.Equal("big.bin", result.GlobalIndex.LargestFiles[0].Name);
        Assert.DoesNotContain(result.GlobalIndex.LargestFolders, entry => ReferenceEquals(entry, result.Root));
        Assert.Contains(result.Root.Flatten(), e => e.FullPath == @"C:\Users\Strife\big.bin" && e.LogicalSizeBytes == 100);
        Assert.Empty(skipped);
    }

    [Fact]
    public void BuildCanReturnRequestedSubtreeFromParentReferenceTree()
    {
        var records = SampleRecords();

        var result = MftTreeBuilder.Build(
            records,
            @"C:\Users",
            new ScanOptions(@"C:\Users", ScanMode.Mft),
            new ProgressThrottle(null),
            [],
            CancellationToken.None,
            FakeSizeResolver);

        Assert.Equal(@"C:\Users", result.Root.FullPath);
        Assert.Equal(2, result.Root.FileCount);
        Assert.Single(result.Root.Children, c => c.IsDirectory);
    }

    private static Dictionary<ulong, NtfsFileRecord> SampleRecords()
    {
        return new Dictionary<ulong, NtfsFileRecord>
        {
            [5] = new(5, 5, FileAttributes.Directory, "."),
            [10] = new(10, 5, FileAttributes.Directory, "Users"),
            [11] = new(11, 10, FileAttributes.Directory, "Strife"),
            [12] = new(12, 11, FileAttributes.Archive, "big.bin"),
            [13] = new(13, 10, FileAttributes.Archive, "small.txt")
        };
    }

    private static (long LogicalSize, long AllocatedSize) FakeSizeResolver(string path, NtfsFileRecord record)
    {
        var size = record.FileName.StartsWith("big", StringComparison.OrdinalIgnoreCase) ? 100 : 25;
        return (size, size);
    }
}
