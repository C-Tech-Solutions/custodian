using System.IO;
using System.Runtime.InteropServices;

namespace Custodian.App.Services;

internal enum RecycleBinMoveResult
{
    Completed,
    Cancelled
}

internal static class RecycleBinService
{
    private const uint FofAllowUndo = 0x0040;
    private const uint FofWantNukeWarning = 0x4000;
    private const uint FofxRecycleOnDelete = 0x00080000;
    private const int HresultCancelled = unchecked((int)0x800704C7);
    private static readonly Guid FileOperationClassId = new("3AD05575-8857-4850-9277-11B85BDB8E09");

    public static RecycleBinMoveResult MoveToRecycleBin(string path, IntPtr ownerHandle)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path is required.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException("The selected file or folder no longer exists.", fullPath);
        }

        var operation = CreateFileOperation();
        IShellItem? item = null;
        try
        {
            ThrowIfFailed(operation.SetOwnerWindow(ownerHandle));
            ThrowIfFailed(operation.SetOperationFlags(FofAllowUndo | FofWantNukeWarning | FofxRecycleOnDelete));

            item = CreateShellItem(fullPath);
            ThrowIfFailed(operation.DeleteItem(item, IntPtr.Zero));

            var hr = operation.PerformOperations();
            if (hr == HresultCancelled)
            {
                return RecycleBinMoveResult.Cancelled;
            }

            ThrowIfFailed(hr);
            ThrowIfFailed(operation.GetAnyOperationsAborted(out var aborted));
            return aborted ? RecycleBinMoveResult.Cancelled : RecycleBinMoveResult.Completed;
        }
        finally
        {
            if (item is not null)
            {
                Marshal.ReleaseComObject(item);
            }

            Marshal.ReleaseComObject(operation);
        }
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
