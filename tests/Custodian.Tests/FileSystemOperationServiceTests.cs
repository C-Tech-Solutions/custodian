using Custodian.Platform.Windows.Services;

namespace Custodian.Tests;

public sealed class FileSystemOperationServiceTests
{
    private const uint FofAllowUndo = 0x0040;

    [Fact]
    public void PermanentDeleteDoesNotAllowUndo()
    {
        var flags = FileSystemOperationService.OperationFlagsFor(FileSystemOperationKind.PermanentDelete);

        Assert.Equal(0u, flags);
        Assert.Equal(0u, flags & FofAllowUndo);
    }

    [Fact]
    public void RecycleDeleteAllowsUndo()
    {
        var flags = FileSystemOperationService.OperationFlagsFor(FileSystemOperationKind.Recycle);

        Assert.NotEqual(0u, flags);
        Assert.Equal(FofAllowUndo, flags & FofAllowUndo);
    }
}
