using Custodian.App;

public sealed class MainWindowTargetRepairTests
{
    [Fact]
    public void FindDriveByVolumeLabelMatchesPathLikeDriveLabelBeforePathRejection()
    {
        var drive = new DriveRow(@"D:\ Library", @"D:\", "1 GB used", "2 GB free", 50);

        var match = MainWindow.FindDriveByVolumeLabel([drive], @"D:\ Library");

        Assert.Same(drive, match);
    }

    [Fact]
    public void FindDriveByVolumeLabelMatchesBareVolumeLabel()
    {
        var drive = new DriveRow(@"D:\ Library", @"D:\", "1 GB used", "2 GB free", 50);

        var match = MainWindow.FindDriveByVolumeLabel([drive], "Library");

        Assert.Same(drive, match);
    }
}
