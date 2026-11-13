using Custodian.Core.Formatting;
using Custodian.Core.Model;
using Custodian.Core.Presentation;

namespace Custodian.Tests;

public sealed class ScanUiPreparationTests
{
    [Fact]
    public void FlattenReturnsEntriesInPreOrder()
    {
        var root = BuildTree(seed: 7, folders: 400);

        var expected = new List<FileSystemEntry>();
        CollectPreOrder(root, expected);

        Assert.Equal(expected, root.Flatten().ToList());
    }

    [Fact]
    public void FlattenHandlesVeryDeepTrees()
    {
        const int depth = 200_000;
        var root = Folder(@"C:\", size: 0);
        var parent = root;
        for (var i = 0; i < depth; i++)
        {
            var child = Folder($@"C:\d{i}", size: 0);
            parent.Children.Add(child);
            parent = child;
        }

        Assert.Equal(depth + 1, root.Flatten().Count());
    }

    [Fact]
    public void FolderNodeBuildsChildrenOnFirstAccess()
    {
        var root = Folder(@"C:\", size: 300);
        root.Children.Add(Folder(@"C:\small", size: 100));
        var node = FolderNode.From(root, parentBytes: 300);

        // Added after From: only a node that builds its children on access sees it.
        root.Children.Add(Folder(@"C:\large", size: 200));
        root.Children.Add(FileEntry(@"C:\file.bin", size: 50));

        var children = node.Children;

        Assert.Equal(["large", "small"], children.Select(child => child.Name).ToList());
        Assert.Same(children, node.Children);
    }

    [Fact]
    public void FolderNodeChildrenMatchTheEagerTree()
    {
        var root = BuildTree(seed: 11, folders: 300);

        var lazy = FolderNode.From(root, Math.Max(1, root.LogicalSizeBytes));

        AssertSameNodes(EagerNode(root, Math.Max(1, root.LogicalSizeBytes)), lazy);
    }

    [Fact]
    public void FolderNodeChildrenCanBeSetExplicitly()
    {
        var child = new FolderNode { Name = "child" };

        var node = new FolderNode { Children = [child] };

        Assert.Same(child, Assert.Single(node.Children));
    }

    [Fact]
    public void FolderJumpIndexMatchesSortedFlattenedFolders()
    {
        var root = BuildTree(seed: 3, folders: 500);

        var preOrder = new List<FileSystemEntry>();
        CollectPreOrder(root, preOrder);
        var expected = preOrder
            .Where(entry => entry.IsDirectory)
            .OrderBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var index = ScanViewProjector.FolderJumpIndex(root);

        Assert.Equal(expected, index.Select(row => row.Entry).ToList());
    }

    [Fact]
    public void FolderJumpIndexKeepsTreeOrderForPathsThatDifferOnlyInCase()
    {
        var root = Folder(@"C:\", size: 0);
        var upper = Folder(@"C:\Data", size: 0);
        var lower = Folder(@"C:\data", size: 0);
        root.Children.Add(upper);
        root.Children.Add(lower);

        var index = ScanViewProjector.FolderJumpIndex(root);

        Assert.Equal([root, upper, lower], index.Select(row => row.Entry).ToList());
    }

    [Fact]
    public void FolderJumpRowSizeFollowsTheEntry()
    {
        var entry = Folder(@"C:\Data", size: 1024);
        var row = FolderJumpRow.From(entry);

        entry.LogicalSizeBytes = 4096;

        Assert.Equal(SizeFormatter.Format(4096), row.Size);
    }

    [Fact]
    public async Task BackgroundIndexSearchMatchesTheSynchronousIndex()
    {
        var root = BuildTree(seed: 5, folders: 400);
        var index = BackgroundFolderJumpIndex.Start(root);

        var rows = await index.SearchAsync("f1", take: 1_000);

        Assert.True(index.IsBuilt);
        Assert.NotEmpty(rows);
        Assert.Equal(
            ScanViewProjector.FolderJumpRows(ScanViewProjector.FolderJumpIndex(root), "f1", take: 1_000).Select(row => row.Entry),
            rows.Select(row => row.Entry));
    }

    [Fact]
    public async Task EmptyBackgroundIndexHasNoRows()
    {
        var rows = await BackgroundFolderJumpIndex.Empty.SearchAsync(string.Empty);

        Assert.True(BackgroundFolderJumpIndex.Empty.IsBuilt);
        Assert.Empty(rows);
    }

    private static void CollectPreOrder(FileSystemEntry entry, List<FileSystemEntry> output)
    {
        output.Add(entry);
        foreach (var child in entry.Children)
        {
            CollectPreOrder(child, output);
        }
    }

    // FolderNode.From as it was before children became lazy.
    private static FolderNode EagerNode(FileSystemEntry entry, long parentBytes)
    {
        var node = FolderNode.From(entry, parentBytes);
        return new FolderNode
        {
            Name = node.Name,
            FullPath = node.FullPath,
            Size = node.Size,
            Percent = node.Percent,
            PercentText = node.PercentText,
            Entry = entry,
            Children = entry.Children
                .Where(child => child.IsDirectory)
                .OrderByDescending(child => child.LogicalSizeBytes)
                .ThenBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                .Select(child => EagerNode(child, Math.Max(1, entry.LogicalSizeBytes)))
                .ToList()
        };
    }

    private static void AssertSameNodes(FolderNode expected, FolderNode actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.FullPath, actual.FullPath);
        Assert.Equal(expected.Size, actual.Size);
        Assert.Equal(expected.Percent, actual.Percent);
        Assert.Equal(expected.PercentText, actual.PercentText);
        Assert.Same(expected.Entry, actual.Entry);
        Assert.Equal(expected.Children.Count, actual.Children.Count);
        for (var i = 0; i < expected.Children.Count; i++)
        {
            AssertSameNodes(expected.Children[i], actual.Children[i]);
        }
    }

    /// <summary>
    /// A tree of <paramref name="folders"/> folders with files mixed in, names that repeat
    /// with different case, and sizes that tie, so ordering rules are exercised.
    /// </summary>
    private static FileSystemEntry BuildTree(int seed, int folders)
    {
        var random = new Random(seed);
        var root = Folder(@"C:\", size: 0);
        var all = new List<FileSystemEntry> { root };
        for (var i = 1; i < folders; i++)
        {
            var parent = all[random.Next(all.Count)];
            var name = random.Next(4) switch
            {
                0 => $"F{random.Next(20)}",
                1 => $"f{random.Next(20)}",
                2 => $"f{random.Next(20)} x",
                _ => $"f{random.Next(20)}.d"
            };
            var folder = Folder(Path.Join(parent.FullPath, name), size: random.Next(5) * 100);
            parent.Children.Add(folder);
            all.Add(folder);

            if (random.Next(2) == 0)
            {
                parent.Children.Add(FileEntry(Path.Join(parent.FullPath, $"file{i}.bin"), size: random.Next(1_000)));
            }
        }

        return root;
    }

    private static FileSystemEntry Folder(string path, long size)
    {
        return new FileSystemEntry
        {
            Name = Path.GetFileName(path.TrimEnd('\\')),
            FullPath = path,
            IsDirectory = true,
            LogicalSizeBytes = size
        };
    }

    private static FileSystemEntry FileEntry(string path, long size)
    {
        return new FileSystemEntry
        {
            Name = Path.GetFileName(path),
            FullPath = path,
            LogicalSizeBytes = size
        };
    }
}
