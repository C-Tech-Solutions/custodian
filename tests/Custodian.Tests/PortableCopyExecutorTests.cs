using Custodian.Core.Model;
using Custodian.Core.Portable;

namespace Custodian.Tests;

public sealed class PortableCopyExecutorTests
{
    [Fact]
    public async Task CopyAsyncCopiesFilesFromProvider()
    {
        using var temp = new TempDirectory();
        var entry = PortableFile("Pixel/Internal shared storage/photo.jpg", "stale-id", "persistent-photo");
        var provider = new FakeStreamProvider(entry =>
            entry.PortablePersistentId == "persistent-photo"
                ? new MemoryStream([1, 2, 3, 4])
                : null);
        var plan = PortableCopyPlanner.BuildPlan([entry], temp.Path);

        var result = await new PortableCopyExecutor(provider).CopyAsync(plan);

        Assert.Equal(1, result.FilesCopied);
        Assert.Equal(0, result.FilesSkipped);
        Assert.Equal([1, 2, 3, 4], await System.IO.File.ReadAllBytesAsync(Path.Combine(temp.Path, "photo.jpg")));
    }

    [Fact]
    public async Task CopyAsyncDeletesPartialFileAfterStreamFailure()
    {
        using var temp = new TempDirectory();
        var entry = PortableFile("Pixel/Internal shared storage/broken.bin", "broken", "broken-persistent");
        var plan = PortableCopyPlanner.BuildPlan([entry], temp.Path);
        var provider = new FakeStreamProvider(_ => new ThrowAfterFirstReadStream());

        var result = await new PortableCopyExecutor(provider).CopyAsync(plan);

        Assert.Equal(0, result.FilesCopied);
        Assert.Equal(1, result.FilesSkipped);
        Assert.False(System.IO.File.Exists(Path.Combine(temp.Path, "broken.bin")));
    }

    [Fact]
    public async Task CopyAsyncDeletesPartialFileAfterCancellation()
    {
        using var temp = new TempDirectory();
        var entry = PortableFile("Pixel/Internal shared storage/cancel.bin", "cancel", "cancel-persistent");
        var plan = PortableCopyPlanner.BuildPlan([entry], temp.Path);
        var provider = new FakeStreamProvider(_ => new CancelAfterFirstReadStream());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new PortableCopyExecutor(provider).CopyAsync(plan));

        Assert.False(System.IO.File.Exists(Path.Combine(temp.Path, "cancel.bin")));
    }

    [Fact]
    public async Task CopyAsyncDoesNotDeleteExistingFileWhenCreateNewFails()
    {
        using var temp = new TempDirectory();
        var destination = Path.Combine(temp.Path, "existing.bin");
        await System.IO.File.WriteAllBytesAsync(destination, [9, 8, 7]);

        var entry = PortableFile("Pixel/Internal shared storage/existing.bin", "existing", "existing-persistent");
        var plan = new PortableCopyPlan(
            [new PortableCopyPlanItem(entry, entry.Name, destination)],
            []);
        var provider = new FakeStreamProvider(_ => new MemoryStream([1, 2, 3]));

        var result = await new PortableCopyExecutor(provider).CopyAsync(plan);

        Assert.Equal(0, result.FilesCopied);
        Assert.Equal(1, result.FilesSkipped);
        Assert.Equal([9, 8, 7], await System.IO.File.ReadAllBytesAsync(destination));
    }

    private static FileSystemEntry PortableFile(string path, string objectId, string persistentId) => new()
    {
        Name = path.Split('/').Last(),
        FullPath = path,
        IsDirectory = false,
        PortableObjectId = objectId,
        PortablePersistentId = persistentId
    };

    private sealed class FakeStreamProvider(Func<FileSystemEntry, Stream?> open) : IPortableObjectStreamProvider
    {
        public Task<Stream?> OpenReadAsync(FileSystemEntry entry, CancellationToken cancellationToken)
            => Task.FromResult(open(entry));
    }

    private sealed class ThrowAfterFirstReadStream : Stream
    {
        private bool _hasRead;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_hasRead)
            {
                throw new IOException("Simulated stream failure.");
            }

            _hasRead = true;
            buffer.Span[0] = 42;
            return ValueTask.FromResult(1);
        }
    }

    private sealed class CancelAfterFirstReadStream : Stream
    {
        private bool _hasRead;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_hasRead)
            {
                throw new OperationCanceledException();
            }

            _hasRead = true;
            buffer.Span[0] = 42;
            return ValueTask.FromResult(1);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"custodian-copy-test-{Guid.NewGuid():N}");

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
