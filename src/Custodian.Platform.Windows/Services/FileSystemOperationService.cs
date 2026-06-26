using System.Runtime.InteropServices;

namespace Custodian.Platform.Windows.Services;

internal enum FileSystemOperationKind
{
    Copy,
    Move,
    Recycle
}

internal sealed record FileSystemOperationFailure(string Path, string Reason);

internal sealed record FileSystemOperationBatchResult(
    int RequestedCount,
    int CompletedCount,
    int CancelledCount,
    int IndeterminateCount,
    IReadOnlyList<FileSystemOperationFailure> Failures)
{
    public bool HasIssues => CancelledCount > 0 || IndeterminateCount > 0 || Failures.Count > 0;
}

internal static class FileSystemOperationService
{
    private const uint FofAllowUndo = 0x0040;
    private const uint FofWantNukeWarning = 0x4000;
    private const uint FofxRecycleOnDelete = 0x00080000;
    private const int HresultCancelled = unchecked((int)0x800704C7);
    private const int HresultCopyEngineUserCancelled = unchecked((int)0x80270000);
    private static readonly Guid FileOperationClassId = new("3AD05575-8857-4850-9277-11B85BDB8E09");

    public static Task<FileSystemOperationBatchResult> CopyToFolderAsync(
        IReadOnlyCollection<string> paths,
        string destinationFolder,
        IntPtr ownerHandle,
        CancellationToken cancellationToken = default)
        => RunOnShellStaThreadAsync(
            () => ExecuteBatch(paths, destinationFolder, ownerHandle, FileSystemOperationKind.Copy, cancellationToken),
            cancellationToken);

    public static Task<FileSystemOperationBatchResult> MoveToFolderAsync(
        IReadOnlyCollection<string> paths,
        string destinationFolder,
        IntPtr ownerHandle,
        CancellationToken cancellationToken = default)
        => RunOnShellStaThreadAsync(
            () => ExecuteBatch(paths, destinationFolder, ownerHandle, FileSystemOperationKind.Move, cancellationToken),
            cancellationToken);

    public static Task<FileSystemOperationBatchResult> MoveToRecycleBinAsync(
        IReadOnlyCollection<string> paths,
        IntPtr ownerHandle,
        CancellationToken cancellationToken = default)
        => RunOnShellStaThreadAsync(
            () => ExecuteBatch(paths, destinationFolder: null, ownerHandle, FileSystemOperationKind.Recycle, cancellationToken),
            cancellationToken);

    private static FileSystemOperationBatchResult ExecuteBatch(
        IReadOnlyCollection<string> paths,
        string? destinationFolder,
        IntPtr ownerHandle,
        FileSystemOperationKind operationKind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var failures = new List<FileSystemOperationFailure>();
        var requestedCount = paths.Count(path => !string.IsNullOrWhiteSpace(path));
        var sourcePaths = DistinctPaths(paths, failures);

        var normalizedDestination = NormalizeDestination(destinationFolder, operationKind);
        var operationResult = ExecuteShellBatch(
            sourcePaths,
            normalizedDestination,
            ownerHandle,
            operationKind,
            failures,
            cancellationToken);
        var completedCount = operationResult.Completed ? operationResult.StagedCount : 0;
        var indeterminateCount = operationResult.Completed ? 0 : operationResult.StagedCount;

        return new FileSystemOperationBatchResult(
            requestedCount,
            completedCount,
            0,
            indeterminateCount,
            failures);
    }

    private static List<string> DistinctPaths(
        IEnumerable<string> paths,
        ICollection<FileSystemOperationFailure> failures)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                if (seen.Add(fullPath))
                {
                    normalized.Add(fullPath);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                failures.Add(new FileSystemOperationFailure(path, ex.Message));
            }
        }

        return normalized;
    }

    private static ShellBatchResult ExecuteShellBatch(
        IReadOnlyList<string> sourcePaths,
        string? destinationFolder,
        IntPtr ownerHandle,
        FileSystemOperationKind operationKind,
        List<FileSystemOperationFailure> failures,
        CancellationToken cancellationToken)
    {
        if (sourcePaths.Count == 0)
        {
            return new ShellBatchResult(0, Completed: true);
        }

        var operation = CreateFileOperation();
        IShellItem? destinationItem = null;
        var sourceItems = new List<IShellItem>();
        try
        {
            ThrowIfFailed(operation.SetOwnerWindow(ownerHandle));
            if (operationKind == FileSystemOperationKind.Recycle)
            {
                ThrowIfFailed(operation.SetOperationFlags(FofAllowUndo | FofWantNukeWarning | FofxRecycleOnDelete));
            }

            if (operationKind is FileSystemOperationKind.Copy or FileSystemOperationKind.Move)
            {
                destinationItem = CreateShellItem(destinationFolder!);
            }

            foreach (var path in sourcePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StageShellItem(path, destinationItem, operation, operationKind, sourceItems, failures);
            }

            if (sourceItems.Count == 0)
            {
                return new ShellBatchResult(0, Completed: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var hr = operation.PerformOperations();
            if (IsCancellationHResult(hr))
            {
                return new ShellBatchResult(sourceItems.Count, Completed: false);
            }

            ThrowIfFailed(hr);
            ThrowIfFailed(operation.GetAnyOperationsAborted(out var aborted));
            return new ShellBatchResult(sourceItems.Count, Completed: !aborted);
        }
        finally
        {
            foreach (var sourceItem in sourceItems)
            {
                Marshal.ReleaseComObject(sourceItem);
            }

            if (destinationItem is not null)
            {
                Marshal.ReleaseComObject(destinationItem);
            }

            Marshal.ReleaseComObject(operation);
        }
    }

    private static void StageShellItem(
        string path,
        IShellItem? destinationItem,
        IFileOperation operation,
        FileSystemOperationKind operationKind,
        ICollection<IShellItem> sourceItems,
        ICollection<FileSystemOperationFailure> failures)
    {
        IShellItem? sourceItem = null;
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                failures.Add(new FileSystemOperationFailure(path, "The file or folder no longer exists."));
                return;
            }

            sourceItem = CreateShellItem(path);
            switch (operationKind)
            {
                case FileSystemOperationKind.Copy:
                    ThrowIfFailed(operation.CopyItem(sourceItem, destinationItem!, null, IntPtr.Zero));
                    break;
                case FileSystemOperationKind.Move:
                    ThrowIfFailed(operation.MoveItem(sourceItem, destinationItem!, null, IntPtr.Zero));
                    break;
                case FileSystemOperationKind.Recycle:
                    ThrowIfFailed(operation.DeleteItem(sourceItem, IntPtr.Zero));
                    break;
            }

            sourceItems.Add(sourceItem);
            sourceItem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            failures.Add(new FileSystemOperationFailure(path, ex.Message));
        }
        finally
        {
            if (sourceItem is not null)
            {
                Marshal.ReleaseComObject(sourceItem);
            }
        }
    }

    private static string? NormalizeDestination(string? destinationFolder, FileSystemOperationKind operationKind)
    {
        if (operationKind == FileSystemOperationKind.Recycle)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            throw new ArgumentException("A destination folder is required.", nameof(destinationFolder));
        }

        var fullPath = Path.GetFullPath(destinationFolder);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"The destination folder does not exist: {fullPath}");
        }

        return fullPath;
    }

    private sealed record ShellBatchResult(int StagedCount, bool Completed);

    private static Task<T> RunOnShellStaThreadAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(action());
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            // Keep the process alive if the WPF window closes while the shell is finishing a batch.
            IsBackground = false,
            Name = "Custodian File Operation Shell"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static IShellItem CreateShellItem(string path)
    {
        var shellItemId = typeof(IShellItem).GUID;
        ThrowIfFailed(SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItemId, out var item));
        return item;
    }

    private static IFileOperation CreateFileOperation()
    {
        var operationType = Type.GetTypeFromCLSID(FileOperationClassId, throwOnError: true)
            ?? throw new InvalidOperationException("Windows Shell file operation service is unavailable.");
        return (IFileOperation)Activator.CreateInstance(operationType)!;
    }

    private static void ThrowIfFailed(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    private static bool IsCancellationHResult(int hr)
        => hr is HresultCancelled or HresultCopyEngineUserCancelled;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IShellItem ppv);

    [ComImport]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig]
        int Advise(IntPtr pfops, out uint pdwCookie);

        [PreserveSig]
        int Unadvise(uint dwCookie);

        [PreserveSig]
        int SetOperationFlags(uint dwOperationFlags);

        [PreserveSig]
        int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);

        [PreserveSig]
        int SetProgressDialog(IntPtr popd);

        [PreserveSig]
        int SetProperties(IntPtr pproparray);

        [PreserveSig]
        int SetOwnerWindow(IntPtr hwndOwner);

        [PreserveSig]
        int ApplyPropertiesToItem(IShellItem psiItem);

        [PreserveSig]
        int ApplyPropertiesToItems(IntPtr punkItems);

        [PreserveSig]
        int RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);

        [PreserveSig]
        int RenameItems(IntPtr pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);

        [PreserveSig]
        int MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IntPtr pfopsItem);

        [PreserveSig]
        int MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);

        [PreserveSig]
        int CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IntPtr pfopsItem);

        [PreserveSig]
        int CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);

        [PreserveSig]
        int DeleteItem(IShellItem psiItem, IntPtr pfopsItem);

        [PreserveSig]
        int DeleteItems(IntPtr punkItems);

        [PreserveSig]
        int NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, IntPtr pfopsItem);

        [PreserveSig]
        int PerformOperations();

        [PreserveSig]
        int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetParent(out IShellItem ppsi);

        [PreserveSig]
        int GetDisplayName(uint sigdnName, out IntPtr ppszName);

        [PreserveSig]
        int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        [PreserveSig]
        int Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
