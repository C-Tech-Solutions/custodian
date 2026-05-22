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
    private const uint FofAllowUndo = 0x0040;
    private const uint FofWantNukeWarning = 0x4000;
    private const uint FofxRecycleOnDelete = 0x00080000;
    private const uint SherbNoConfirmation = 0x00000001;
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

    public static Task<IReadOnlyList<RecycleBinEntry>> GetItemsAsync(CancellationToken cancellationToken = default)
        => RunOnShellStaThreadAsync(EnumerateItems, cancellationToken);

    public static Task RestoreAsync(IReadOnlyCollection<RecycleBinEntry> entries, CancellationToken cancellationToken = default)
        => RunOnShellStaThreadAsync(() => InvokeVerbOnEntries(entries, "restore", cancellationToken), cancellationToken);

    public static Task DeletePermanentlyAsync(IReadOnlyCollection<RecycleBinEntry> entries, CancellationToken cancellationToken = default)
        => RunOnShellStaThreadAsync(() => InvokeVerbOnEntries(entries, "delete", cancellationToken), cancellationToken);

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

    private static IReadOnlyList<RecycleBinEntry> EnumerateItems()
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

            var count = Convert.ToInt32(items.Count, CultureInfo.InvariantCulture);
            var entries = new List<RecycleBinEntry>(count);
            for (var index = 0; index < count; index++)
            {
                dynamic? item = null;
                try
                {
                    item = items.Item(index);
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
        var originalLocation = CleanShellText(folder.GetDetailsOf(item, 1));
        var dateDeletedText = CleanShellText(folder.GetDetailsOf(item, 2));
        var itemType = CleanShellText(folder.GetDetailsOf(item, 4));
        var recyclePath = CleanShellText(item.Path);
        var sizeBytes = GetShellItemSize(item);
        var isFolder = GetShellItemIsFolder(item, itemType);
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

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                dynamic? item = null;
                try
                {
                    item = FindRecycleBinItem(folder, entry);
                    InvokeShellVerb(item, verbName);
                }
                finally
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

    private static dynamic FindRecycleBinItem(dynamic folder, RecycleBinEntry entry)
    {
        dynamic? items = null;
        try
        {
            items = folder.Items();
            var count = Convert.ToInt32(items.Count, CultureInfo.InvariantCulture);
            for (var index = 0; index < count; index++)
            {
                dynamic? item = null;
                try
                {
                    item = items.Item(index);
                    if (item is null)
                    {
                        continue;
                    }

                    if (MatchesEntry(folder, item, entry))
                    {
                        var match = item;
                        item = null;
                        return match;
                    }
                }
                finally
                {
                    ReleaseComObject(item);
                }
            }
        }
        finally
        {
            ReleaseComObject(items);
        }

        throw new FileNotFoundException("The selected Recycle Bin item is no longer available.", entry.Name);
    }

    private static bool MatchesEntry(dynamic folder, dynamic item, RecycleBinEntry entry)
    {
        var recyclePath = CleanShellText(item.Path);
        if (!string.IsNullOrWhiteSpace(recyclePath)
            && string.Equals(recyclePath, entry.RecyclePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var originalLocation = CleanShellText(folder.GetDetailsOf(item, 1));
        var dateDeletedText = CleanShellText(folder.GetDetailsOf(item, 2));
        var itemType = CleanShellText(folder.GetDetailsOf(item, 4));
        var name = RestoreHiddenExtension(
            CleanShellText(item.Name),
            recyclePath,
            GetShellItemIsFolder(item, itemType));
        var stableKey = BuildStableKey(recyclePath, originalLocation, name, dateDeletedText);
        return string.Equals(stableKey, entry.StableKey, StringComparison.OrdinalIgnoreCase);
    }

    private static void InvokeShellVerb(dynamic item, string verbName)
    {
        dynamic? verbs = null;
        try
        {
            verbs = item.Verbs();
            var count = Convert.ToInt32(verbs.Count, CultureInfo.InvariantCulture);
            for (var index = 0; index < count; index++)
            {
                dynamic? verb = null;
                try
                {
                    verb = verbs.Item(index);
                    var normalized = NormalizeVerbName(CleanShellText(verb.Name));
                    if (string.Equals(normalized, verbName, StringComparison.OrdinalIgnoreCase))
                    {
                        verb.DoIt();
                        return;
                    }
                }
                finally
                {
                    ReleaseComObject(verb);
                }
            }
        }
        finally
        {
            ReleaseComObject(verbs);
        }

        throw new InvalidOperationException($"Windows did not expose a {verbName} action for the selected Recycle Bin item.");
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
                completion.SetResult(action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
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

    private static string NormalizeVerbName(string value)
    {
        return value
            .Replace("&", string.Empty, StringComparison.Ordinal)
            .Replace("...", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();
    }

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
