using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Custodian.Core.Model;

namespace Custodian.App.Services;

internal enum RecycleBinMoveResult
{
    Completed,
    Cancelled
}

internal static class RecycleBinService
{
    private const int RecycleBinShellNamespace = 10;
    private const int RecycleBinColumnOriginalLocation = 1;
    private const int RecycleBinColumnDateDeleted = 2;
    private const int RecycleBinColumnItemType = 4;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofWantNukeWarning = 0x4000;
    private const uint FofxRecycleOnDelete = 0x00080000;
    private const uint SherbNoConfirmation = 0x00000001;
    private const int HresultCancelled = unchecked((int)0x800704C7);
    private const string RestoreCanonicalVerb = "undelete";
    private const string DeleteCanonicalVerb = "delete";
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

    public static Task<IReadOnlyList<RecycleBinEntry>> GetItemsAsync(CancellationToken cancellationToken = default)
        => RunOnShellStaThreadAsync(() => EnumerateItems(cancellationToken), cancellationToken);

    public static Task<(long SizeBytes, long ItemCount)> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new ShQueryRecycleBinInfo
            {
                CbSize = Marshal.SizeOf<ShQueryRecycleBinInfo>()
            };

            ThrowIfFailed(SHQueryRecycleBin(null, ref info));
            return (Math.Max(0, info.SizeBytes), Math.Max(0, info.ItemCount));
        }, cancellationToken);
    }

    public static Task RestoreAsync(IReadOnlyCollection<RecycleBinEntry> entries, CancellationToken cancellationToken = default)
        => RunOnShellStaThreadAsync(() => InvokeVerbOnEntries(entries, RestoreCanonicalVerb, cancellationToken), cancellationToken);

    public static Task DeletePermanentlyAsync(IReadOnlyCollection<RecycleBinEntry> entries, CancellationToken cancellationToken = default)
        => RunOnShellStaThreadAsync(() => InvokeVerbOnEntries(entries, DeleteCanonicalVerb, cancellationToken), cancellationToken);

    public static Task EmptyAsync(IntPtr ownerHandle, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfFailed(SHEmptyRecycleBin(ownerHandle, null, SherbNoConfirmation));
        }, cancellationToken);
    }

    public static void OpenInExplorer()
    {
        Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder")
        {
            UseShellExecute = true
        });
    }

    private static IReadOnlyList<RecycleBinEntry> EnumerateItems(CancellationToken cancellationToken)
    {
        dynamic? shell = null;
        dynamic? folder = null;
        dynamic? items = null;
        try
        {
            shell = CreateShellApplication();
            folder = shell.NameSpace(RecycleBinShellNamespace)
                ?? throw new InvalidOperationException("Windows Recycle Bin namespace is unavailable.");
            items = folder.Items();

            var entries = new List<RecycleBinEntry>();
            int count = items.Count;
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                dynamic? item = TryGetShellCollectionItem(items, index);
                try
                {
                    if (item is null)
                    {
                        continue;
                    }

                    entries.Add(CreateEntry(folder, item));
                }
                finally
                {
                    ReleaseComObject(item);
                }
            }

            return entries;
        }
        finally
        {
            ReleaseComObject(items);
            ReleaseComObject(folder);
            ReleaseComObject(shell);
        }
    }

    private static RecycleBinEntry CreateEntry(dynamic folder, dynamic item)
    {
        var name = CleanShellText(item.Name);
        var originalLocation = CleanShellText(folder.GetDetailsOf(item, RecycleBinColumnOriginalLocation));
        var dateDeletedText = CleanShellText(folder.GetDetailsOf(item, RecycleBinColumnDateDeleted));
        var itemType = CleanShellText(folder.GetDetailsOf(item, RecycleBinColumnItemType));
        var recyclePath = CleanShellText((object?)item.Path);
        var isFolder = GetShellItemIsFolder(item, itemType);
        var sizeBytes = isFolder ? 0 : GetShellItemSize(item);
        name = RestoreHiddenExtension(name, recyclePath, isFolder);

        return new RecycleBinEntry(
            name,
            originalLocation,
            ParseShellDate(dateDeletedText),
            sizeBytes,
            itemType,
            recyclePath,
            isFolder,
            BuildStableKey(recyclePath, originalLocation, name, dateDeletedText));
    }

    private static void InvokeVerbOnEntries(
        IReadOnlyCollection<RecycleBinEntry> entries,
        string verbName,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return;
        }

        dynamic? shell = null;
        dynamic? folder = null;
        try
        {
            shell = CreateShellApplication();
            folder = shell.NameSpace(RecycleBinShellNamespace)
                ?? throw new InvalidOperationException("Windows Recycle Bin namespace is unavailable.");

            var matchedItems = FindRecycleBinItems(folder, entries, cancellationToken);
            try
            {
                foreach (var item in matchedItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    InvokeShellVerb(item, verbName);
                }
            }
            finally
            {
                foreach (var item in matchedItems)
                {
                    ReleaseComObject(item);
                }
            }
        }
        finally
        {
            ReleaseComObject(folder);
            ReleaseComObject(shell);
        }
    }

    private static IReadOnlyList<object> FindRecycleBinItems(
        dynamic folder,
        IReadOnlyCollection<RecycleBinEntry> entries,
        CancellationToken cancellationToken)
    {
        var requestedRecyclePaths = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.RecyclePath))
            .ToDictionary(entry => entry.RecyclePath, StringComparer.OrdinalIgnoreCase);
        var requestedStableKeys = entries
            .ToDictionary(entry => entry.StableKey, StringComparer.OrdinalIgnoreCase);
        var matchedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedItems = new List<object>();

        dynamic? items = null;
        try
        {
            items = folder.Items();
            int count = items.Count;
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                dynamic? item = TryGetShellCollectionItem(items, index);
                var keepItem = false;
                try
                {
                    if (item is null)
                    {
                        continue;
                    }

                    var itemKey = GetRequestedEntryKey(folder, item, requestedRecyclePaths, requestedStableKeys);
                    if (itemKey is null || !matchedKeys.Add(itemKey))
                    {
                        continue;
                    }

                    matchedItems.Add(item);
                    keepItem = true;
                }
                finally
                {
                    if (!keepItem)
                    {
                        ReleaseComObject(item);
                    }
                }
            }
        }
        finally
        {
            ReleaseComObject(items);
        }

        if (matchedItems.Count != entries.Count)
        {
            foreach (var item in matchedItems)
            {
                ReleaseComObject(item);
            }

            throw new FileNotFoundException("One or more selected Recycle Bin items are no longer available.");
        }

        return matchedItems;
    }

    private static string? GetRequestedEntryKey(
        dynamic folder,
        dynamic item,
        IReadOnlyDictionary<string, RecycleBinEntry> requestedRecyclePaths,
        IReadOnlyDictionary<string, RecycleBinEntry> requestedStableKeys)
    {
        var recyclePath = CleanShellText(item.Path);
        if (!string.IsNullOrWhiteSpace(recyclePath))
        {
            RecycleBinEntry recyclePathEntry = null!;
            if (requestedRecyclePaths.TryGetValue(recyclePath, out recyclePathEntry))
            {
                return recyclePathEntry.StableKey;
            }
        }

        var originalLocation = CleanShellText((object?)folder.GetDetailsOf(item, RecycleBinColumnOriginalLocation));
        var dateDeletedText = CleanShellText((object?)folder.GetDetailsOf(item, RecycleBinColumnDateDeleted));
        var itemType = CleanShellText((object?)folder.GetDetailsOf(item, RecycleBinColumnItemType));
        var name = RestoreHiddenExtension(
            CleanShellText((object?)item.Name),
            recyclePath,
            GetShellItemIsFolder(item, itemType));
        var stableKey = BuildStableKey(recyclePath, originalLocation, name, dateDeletedText);
        RecycleBinEntry stableKeyEntry = null!;
        return requestedStableKeys.TryGetValue(stableKey, out stableKeyEntry)
            ? stableKeyEntry.StableKey
            : null;
    }

    private static void InvokeShellVerb(dynamic item, string verbName)
    {
        try
        {
            item.InvokeVerbEx(verbName);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException($"Windows did not expose the {verbName} action for the selected Recycle Bin item.", ex);
        }
    }

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
            IsBackground = true,
            Name = "Custodian Recycle Bin Shell"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(cancellationToken);
    }

    private static Task RunOnShellStaThreadAsync(Action action, CancellationToken cancellationToken)
        => RunOnShellStaThreadAsync(() =>
        {
            action();
            return true;
        }, cancellationToken);

    private static dynamic CreateShellApplication()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application", throwOnError: true)
            ?? throw new InvalidOperationException("Windows Shell application service is unavailable.");
        return Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Windows Shell application service could not be created.");
    }

    private static dynamic? TryGetShellCollectionItem(dynamic items, int index)
    {
        try
        {
            return items.Item(index);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static long GetShellItemSize(dynamic item)
    {
        try
        {
            return Math.Max(0, Convert.ToInt64(item.Size, CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or COMException)
        {
            return 0;
        }
    }

    private static bool GetShellItemIsFolder(dynamic item, string itemType)
    {
        if (itemType.Contains("zip", StringComparison.OrdinalIgnoreCase)
            || itemType.Contains("archive", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            return Convert.ToBoolean(item.IsFolder, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or COMException)
        {
            return itemType.Contains("folder", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string RestoreHiddenExtension(string name, string recyclePath, bool isFolder)
    {
        if (isFolder
            || string.IsNullOrWhiteSpace(name)
            || !string.IsNullOrWhiteSpace(Path.GetExtension(name)))
        {
            return name;
        }

        var extension = Path.GetExtension(recyclePath);
        return string.IsNullOrWhiteSpace(extension) ? name : name + extension;
    }

    private static DateTimeOffset? ParseShellDate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTime.TryParse(
            text,
            CultureInfo.CurrentCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
            out var date)
            ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Local))
            : null;
    }

    private static string BuildStableKey(
        string recyclePath,
        string originalLocation,
        string name,
        string dateDeletedText)
        => string.Join("|", recyclePath, originalLocation, name, dateDeletedText);

    private static string CleanShellText(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.CurrentCulture);
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch == '\0' || char.GetUnicodeCategory(ch) == UnicodeCategory.Format)
            {
                continue;
            }

            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value))
        {
            return;
        }

        Marshal.ReleaseComObject(value);
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
    private static extern int SHQueryRecycleBin(
        [MarshalAs(UnmanagedType.LPWStr)] string? pszRootPath,
        ref ShQueryRecycleBinInfo pSHQueryRbInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHEmptyRecycleBin(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPWStr)] string? pszRootPath,
        uint dwFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        out IShellItem ppv);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShQueryRecycleBinInfo
    {
        public int CbSize;
        public long SizeBytes;
        public long ItemCount;
    }

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
