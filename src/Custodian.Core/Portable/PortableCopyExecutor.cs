using System.Buffers;
using System.Runtime.InteropServices;
using Custodian.Core.Model;

namespace Custodian.Core.Portable;

public sealed class PortableCopyExecutor(IPortableObjectStreamProvider streamProvider)
{
    private const int CopyBufferSize = 128 * 1024;

    public async Task<PortableCopyResult> CopyAsync(
        PortableCopyPlan plan,
        IProgress<PortableCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var skipped = new List<SkippedEntry>(plan.SkippedEntries);
        long filesCopied = 0;
        long bytesCopied = 0;

        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new PortableCopyProgress(
                item.Entry.FullPath,
                filesCopied,
                skipped.Count,
                bytesCopied,
                "Copying phone files"));

            try
            {
                if (item.IsDirectory)
                {
                    Directory.CreateDirectory(item.DestinationPath);
                    continue;
                }

                await using var source = await streamProvider.OpenReadAsync(item.Entry, cancellationToken).ConfigureAwait(false);
                if (source is null)
                {
                    skipped.Add(new SkippedEntry(item.Entry.FullPath, "The phone item was not found. Rescan this phone and try again."));
                    continue;
                }

                var destinationDirectory = Path.GetDirectoryName(item.DestinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                var copied = await CopyOneFileAsync(source, item.DestinationPath, cancellationToken).ConfigureAwait(false);
                filesCopied++;
                bytesCopied += copied;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ExternalException)
            {
                skipped.Add(new SkippedEntry(item.Entry.FullPath, ex.Message));
            }
        }

        progress?.Report(new PortableCopyProgress(
            string.Empty,
            filesCopied,
            skipped.Count,
            bytesCopied,
            "Phone copy complete"));

        return new PortableCopyResult(filesCopied, skipped.Count, bytesCopied, skipped);
    }

    private static async Task<long> CopyOneFileAsync(
        Stream source,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long bytesCopied = 0;
        FileStream? destination = null;
        var deletePartialFile = false;
        var success = false;
        try
        {
            destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            deletePartialFile = true;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                bytesCopied += read;
            }

            success = true;
            return bytesCopied;
        }
        finally
        {
            if (destination is not null)
            {
                if (success)
                {
                    await destination.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    try
                    {
                        await destination.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // Preserve the original copy failure; cleanup below is best-effort.
                    }
                }
            }

            ArrayPool<byte>.Shared.Return(buffer);

            if (deletePartialFile && !success)
            {
                DeletePartialFile(destinationPath);
            }
        }
    }

    private static void DeletePartialFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup; the copy failure itself is reported to the user.
        }
    }
}
