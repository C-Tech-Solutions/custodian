using Custodian.Core.Model;
using Custodian.Core.Scanning;

namespace Custodian.Tests;

public sealed class MftTreeBuilderTests
{
    // Parent references carry a sequence number in their top 16 bits; the root directory is MFT record 5.
    private const ulong RootReference = 5UL | (5UL << 48);

    [Fact]
    public void BuildCreatesTreeFromParentReferencesWithoutDirectoryProbing()
    {
        var records = SampleRecords();
        var skipped = new List<SkippedEntry>();

        var result = MftTreeBuilder.Build(
            records.Values,
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
            records.Values,
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

    [Fact]
    public void BuildSubtreeResolvesOnlyFilesInsideRequestedTree()
    {
        var records = SampleRecords();
        records[20] = new(20, 5, FileAttributes.Directory, "Windows");
        records[21] = new(21, 20, FileAttributes.Archive, "outside.bin");
        var resolvedPaths = new List<string>();

        var result = MftTreeBuilder.Build(
            records.Values,
            @"C:\Users",
            new ScanOptions(@"C:\Users", ScanMode.Mft),
            new ProgressThrottle(null),
            [],
            CancellationToken.None,
            (path, record) =>
            {
                resolvedPaths.Add(path);
                return FakeSizeResolver(path, record);
            });

        Assert.Equal(2, result.FilesSeen);
        Assert.Contains(@"C:\Users\Strife\big.bin", resolvedPaths);
        Assert.Contains(@"C:\Users\small.txt", resolvedPaths);
        Assert.DoesNotContain(@"C:\Windows\outside.bin", resolvedPaths);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildAttachesToVolumeRootOnlyRecordsWhoseParentIsTheRootDirectory(bool includeRootRecord)
    {
        var records = RecordsWithLeftOutFolders(includeRootRecord);
        var skipped = new List<SkippedEntry>();
        var resolvedPaths = new List<string>();

        var result = MftTreeBuilder.Build(
            records.Values,
            @"C:\",
            new ScanOptions(@"C:\", ScanMode.Mft),
            new ProgressThrottle(null),
            skipped,
            CancellationToken.None,
            (path, record) =>
            {
                resolvedPaths.Add(path);
                return FakeSizeResolver(path, record);
            });

        Assert.Equal(
            [@"C:\Users\Strife\big.bin", @"C:\Users\small.txt", @"C:\top.txt"],
            resolvedPaths.Order(StringComparer.Ordinal));
        Assert.Equal(3, result.FilesSeen);
        Assert.Equal(3, result.Root.FileCount);
        Assert.Equal(2, result.Root.DirectoryCount);
        Assert.Equal(["top.txt", "Users"], result.Root.Children.Select(c => c.Name).Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.GlobalIndex.LargestFiles, entry => entry.Name is "cloud.bin" or "photo.jpg" or "orphan.bin" or "lost.bin");
        Assert.Equal(
            [
                new SkippedEntry(@"C:\Users\OneDrive", MftTreeBuilder.ReparsePointReason),
                new SkippedEntry("Lost", MftTreeBuilder.ParentNotEnumeratedReason),
                new SkippedEntry("orphan.bin", MftTreeBuilder.ParentNotEnumeratedReason)
            ],
            skipped.OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildSubtreeReportsOnlyLeftOutFoldersInsideRequestedTree(bool includeRootRecord)
    {
        var records = RecordsWithLeftOutFolders(includeRootRecord);
        records[50] = new(50, RootReference, FileAttributes.Directory | FileAttributes.ReparsePoint, "Junction");
        var skipped = new List<SkippedEntry>();

        var result = MftTreeBuilder.Build(
            records.Values,
            @"C:\Users",
            new ScanOptions(@"C:\Users", ScanMode.Mft),
            new ProgressThrottle(null),
            skipped,
            CancellationToken.None,
            FakeSizeResolver);

        Assert.Equal(@"C:\Users", result.Root.FullPath);
        Assert.Equal(2, result.FilesSeen);
        Assert.Equal([new SkippedEntry(@"C:\Users\OneDrive", MftTreeBuilder.ReparsePointReason)], skipped);
    }

    [Fact]
    public void BuildFindsRequestedSubtreeIgnoringCase()
    {
        var result = MftTreeBuilder.Build(
            SampleRecords().Values,
            @"c:\users\strife",
            new ScanOptions(@"c:\users\strife", ScanMode.Mft),
            new ProgressThrottle(null),
            [],
            CancellationToken.None,
            FakeSizeResolver);

        Assert.Equal(@"C:\Users\Strife", result.Root.FullPath, ignoreCase: true);
        Assert.Equal(1, result.Root.FileCount);
    }

    [Fact]
    public void BuildThrowsWhenRequestedSubtreeIsNotEnumerated()
    {
        Assert.Throws<DirectoryNotFoundException>(() => MftTreeBuilder.Build(
            SampleRecords().Values,
            @"C:\Users\Missing",
            new ScanOptions(@"C:\Users\Missing", ScanMode.Mft),
            new ProgressThrottle(null),
            [],
            CancellationToken.None,
            FakeSizeResolver));
    }

    private static Dictionary<ulong, NtfsFileRecord> RecordsWithLeftOutFolders(bool includeRootRecord)
    {
        var records = new Dictionary<ulong, NtfsFileRecord>
        {
            [10] = new(10, RootReference, FileAttributes.Directory, "Users"),
            [11] = new(11, 10, FileAttributes.Directory, "Strife"),
            [12] = new(12, 11, FileAttributes.Archive, "big.bin"),
            [13] = new(13, 10, FileAttributes.Archive, "small.txt"),
            [14] = new(14, RootReference, FileAttributes.Archive, "top.txt"),

            // A skipped reparse-point folder, with a file and a folder inside it.
            [30] = new(30, 10, FileAttributes.Directory | FileAttributes.ReparsePoint, "OneDrive"),
            [31] = new(31, 30, FileAttributes.Archive, "cloud.bin"),
            [32] = new(32, 30, FileAttributes.Directory, "Photos"),
            [33] = new(33, 32, FileAttributes.Archive, "photo.jpg"),

            // Records whose parent folder is not in the enumeration at all.
            [40] = new(40, 99, FileAttributes.Archive, "orphan.bin"),
            [41] = new(41, 98, FileAttributes.Directory, "Lost"),
            [42] = new(42, 41, FileAttributes.Archive, "lost.bin")
        };

        if (includeRootRecord)
        {
            records[RootReference] = new(RootReference, RootReference, FileAttributes.Directory, ".");
        }

        return records;
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
