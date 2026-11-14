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
    public void RootTiesEveryFolderToItsOwnScanEvenWhenPathsOverlap()
    {
        var drive = Folder(@"C:\");
        var driveUsers = Folder(@"C:\Users");
        driveUsers.Children.Add(Folder(@"C:\Users\Public"));
        drive.Children.Add(driveUsers);
        var usersScan = Folder(@"C:\Users");
        usersScan.Children.Add(Folder(@"C:\Users\Public"));

        var driveRoot = FolderNode.From(drive, parentBytes: 1);
        var usersRoot = FolderNode.From(usersScan, parentBytes: 1);
        var drivePublic = driveRoot.Children.Single().Children.Single();
        var usersPublic = usersRoot.Children.Single();

        Assert.Same(driveRoot, driveRoot.Root);
        Assert.Same(driveRoot, drivePublic.Root);
        Assert.Same(usersRoot, usersPublic.Root);
    }

    [Fact]
    public void RootTellsApartSiblingsWhosePathsDifferOnlyByCase()
    {
        var storage = Folder(@"Phone\Internal storage");
        storage.Children.Add(Folder(@"Phone\Internal storage\DCIM"));
        storage.Children.Add(Folder(@"Phone\Internal storage\dcim"));

        var root = FolderNode.From(storage, parentBytes: 1);

        Assert.Equal(2, root.Children.Count);
        Assert.All(root.Children, child => Assert.Same(root, child.Root));
    }

    [Fact]
    public void ScanHeadingsShowADriveOrFolderIconAndThePathWhenTheNameHidesIt()
    {
        var drive = Folder(@"C:\");
        var users = Folder(@"C:\Users");
        users.Children.Add(Folder(@"C:\Users\Public"));

        var driveRoot = FolderNode.From(drive, parentBytes: 1);
        var usersRoot = FolderNode.From(users, parentBytes: 1);
        var publicFolder = usersRoot.Children.Single();

        Assert.True(driveRoot.IsRoot);
        Assert.Equal("\uEDA2", driveRoot.Icon);
        Assert.Equal(string.Empty, driveRoot.Location);

        Assert.True(usersRoot.IsRoot);
        Assert.Equal("\uE8B7", usersRoot.Icon);
        Assert.Equal(@"C:\Users", usersRoot.Location);

        Assert.False(publicFolder.IsRoot);
        Assert.Equal(string.Empty, publicFolder.Location);
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
