using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Custodian.Core.Model;
using Custodian.Core.Presentation;

namespace Custodian.Tests;

public sealed class FolderMapProjectorTests
{
    [Fact]
    public void OrderRootsSortsDrivesAndPutsFolderScansAfterTheirDrive()
    {
        var d = Root(@"D:\");
        var users = Root(@"C:\Users");
        var c = Root(@"C:\");

        var ordered = FolderMapProjector.OrderRoots([d, users, c]);

        Assert.Equal([c, users, d], ordered);
    }

    [Fact]
    public void SyncRootsAddsANewScanWithoutTouchingTheOthers()
    {
        var c = Root(@"C:\");
        var e = Root(@"E:\");
        var d = Root(@"D:\");
        var target = new ObservableCollection<FolderNode> { c, e };
        var changes = Record(target);

        FolderMapProjector.SyncRoots(target, [c, d, e]);

        Assert.Equal([c, d, e], target);
        var change = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Add, change.Action);
        Assert.Same(d, change.NewItems![0]);
    }

    [Fact]
    public void SyncRootsSwapsOnlyTheRescannedRoot()
    {
        var c = Root(@"C:\");
        var oldD = Root(@"D:\");
        var newD = Root(@"D:\");
        var e = Root(@"E:\");
        var target = new ObservableCollection<FolderNode> { c, oldD, e };
        var changes = Record(target);

        FolderMapProjector.SyncRoots(target, [c, newD, e]);

        Assert.Equal([c, newD, e], target);
        Assert.Equal(2, changes.Count);
        Assert.Equal(NotifyCollectionChangedAction.Remove, changes[0].Action);
        Assert.Same(oldD, changes[0].OldItems![0]);
        Assert.Equal(NotifyCollectionChangedAction.Add, changes[1].Action);
        Assert.Same(newD, changes[1].NewItems![0]);
    }

    [Fact]
    public void SyncRootsRemovesAnEvictedScan()
    {
        var c = Root(@"C:\");
        var d = Root(@"D:\");
        var target = new ObservableCollection<FolderNode> { c, d };
        var changes = Record(target);

        FolderMapProjector.SyncRoots(target, [d]);

        Assert.Equal([d], target);
        var change = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Remove, change.Action);
        Assert.Same(c, change.OldItems![0]);
    }

    [Fact]
    public void SyncRootsIsANoOpWhenNothingChanged()
    {
        var c = Root(@"C:\");
        var d = Root(@"D:\");
        var target = new ObservableCollection<FolderNode> { c, d };
        var changes = Record(target);

        FolderMapProjector.SyncRoots(target, [c, d]);

        Assert.Empty(changes);
    }

    [Fact]
    public void SyncRootsFillsAnEmptyMapAndReordersOutOfPlaceRoots()
    {
        var c = Root(@"C:\");
        var d = Root(@"D:\");
        var e = Root(@"E:\");
        var empty = new List<FolderNode>();
        var shuffled = new List<FolderNode> { e, c, d };

        FolderMapProjector.SyncRoots(empty, [c, d, e]);
        FolderMapProjector.SyncRoots(shuffled, [c, d, e]);

        Assert.Equal([c, d, e], empty);
        Assert.Equal([c, d, e], shuffled);
    }

    [Fact]
    public void ContainsFindsTheRootAndItsOwnFolders()
    {
        var root = Folder(@"C:\");
        var users = Folder(@"C:\Users");
        var publicFolder = Folder(@"C:\Users\Public");
        users.Children.Add(publicFolder);
        root.Children.Add(users);

        Assert.True(FolderMapProjector.Contains(root, root));
        Assert.True(FolderMapProjector.Contains(root, publicFolder));
    }

    [Fact]
    public void ContainsRejectsTheSamePathFromAnotherScan()
    {
        var driveRoot = Folder(@"C:\");
        var driveUsers = Folder(@"C:\Users");
        driveUsers.Children.Add(Folder(@"C:\Users\Public"));
        driveRoot.Children.Add(driveUsers);

        var usersScanRoot = Folder(@"C:\Users");
        var usersScanPublic = Folder(@"C:\Users\Public");
        usersScanRoot.Children.Add(usersScanPublic);

        Assert.False(FolderMapProjector.Contains(driveRoot, usersScanPublic));
        Assert.False(FolderMapProjector.Contains(driveRoot, Folder(@"D:\Data")));
        Assert.True(FolderMapProjector.Contains(usersScanRoot, usersScanPublic));
    }

    private static List<NotifyCollectionChangedEventArgs> Record(ObservableCollection<FolderNode> collection)
    {
        var changes = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, args) => changes.Add(args);
        return changes;
    }

    private static FolderNode Root(string path) => FolderNode.From(Folder(path), parentBytes: 1);

    private static FileSystemEntry Folder(string path)
    {
        return new FileSystemEntry
        {
            Name = Path.GetFileName(path.TrimEnd('\\')),
            FullPath = path,
            IsDirectory = true
        };
    }
}
