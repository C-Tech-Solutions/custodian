using Custodian.Core.Model;
using Custodian.Core.Presentation;
using Custodian.Tui;

namespace Custodian.Tests;

public sealed class TerminalChartRendererTests
{
    [Fact]
    public void RenderHandlesMissingData()
    {
        var output = TerminalChartRenderer.Render(null, width: 40);

        Assert.Equal("No chart data.", output);
    }

    [Fact]
    public void RenderShowsRankedBarsAndSizes()
    {
        var root = new FileSystemEntry
        {
            Name = "Root",
            FullPath = "C:\\",
            IsDirectory = true,
            LogicalSizeBytes = 100
        };
        root.Children.Add(new FileSystemEntry
        {
            Name = "large.bin",
            FullPath = "C:\\large.bin",
            LogicalSizeBytes = 75,
            AllocatedSizeBytes = 75,
            Extension = ".bin"
        });
        root.Children.Add(new FileSystemEntry
        {
            Name = "small.log",
            FullPath = "C:\\small.log",
            LogicalSizeBytes = 25,
            AllocatedSizeBytes = 25,
            Extension = ".log"
        });

        var output = TerminalChartRenderer.Render(ScanViewProjector.SelectedFolderChart(root), width: 40);

        Assert.Contains("Root distribution", output);
        Assert.Contains("large.bin", output);
        Assert.Contains("75.0%", output);
        Assert.Contains("small.log", output);
        Assert.Contains("25.0%", output);
    }
}
