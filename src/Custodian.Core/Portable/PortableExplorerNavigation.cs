using Custodian.Core.Model;

namespace Custodian.Core.Portable;

public enum PortableExplorerOpenMode
{
    Open,
    Reveal
}

public enum PortableExplorerOpenResultKind
{
    OpenedExactItem,
    OpenedParent,
    OpenedStorageRoot,
    OpenedThisPc,
    Failed
}

public sealed record PortableExplorerOpenResult(
    PortableExplorerOpenResultKind Kind,
    string Message = "")
{
    public static PortableExplorerOpenResult Exact(string message = "") =>
        new(PortableExplorerOpenResultKind.OpenedExactItem, message);

    public static PortableExplorerOpenResult Parent(string message = "") =>
        new(PortableExplorerOpenResultKind.OpenedParent, message);

    public static PortableExplorerOpenResult StorageRoot(string message = "") =>
        new(PortableExplorerOpenResultKind.OpenedStorageRoot, message);

    public static PortableExplorerOpenResult ThisPc(string message = "") =>
        new(PortableExplorerOpenResultKind.OpenedThisPc, message);

    public static PortableExplorerOpenResult Failed(string message = "") =>
        new(PortableExplorerOpenResultKind.Failed, message);
}

public interface IPortableExplorerNode
{
    string Name { get; }
    string IdentityText { get; }
    bool IsFolder { get; }
    IReadOnlyList<IPortableExplorerNode> GetChildren();
    bool TryOpen();
    bool TryInvokeDefault();
    bool TrySelectInExplorer();
}

public static class PortableExplorerNavigator
{
    public static IReadOnlyList<string> GetRelativeSegments(ScanResult result, FileSystemEntry entry)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(entry);

        var rootPath = NormalizePortablePath(result.DisplayRootPath);
        var entryPath = NormalizePortablePath(entry.FullPath);
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return SplitPortablePath(entryPath);
        }

        if (string.Equals(entryPath, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (entryPath.Length > rootPath.Length &&
            entryPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) &&
            entryPath[rootPath.Length] == '/')
        {
            return SplitPortablePath(entryPath[(rootPath.Length + 1)..]);
        }

        return [];
    }

    public static PortableExplorerOpenResult Open(
        ScanResult result,
        FileSystemEntry entry,
        IPortableExplorerNode thisPc,
        PortableExplorerOpenMode mode)
    {
        var device = FindChild(thisPc, result.PortableDeviceName, allowContains: true);
        if (device is null)
        {
            return TryOpenThisPc(thisPc);
        }

        var storage = FindChild(device, result.PortableStorageName, allowContains: true);
        if (storage is null)
        {
            return device.TryOpen()
                ? PortableExplorerOpenResult.Parent("Opened the phone device.")
                : TryOpenThisPc(thisPc);
        }

        var segments = GetRelativeSegments(result, entry);
        var current = storage;
        IPortableExplorerNode? parent = null;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (!current.IsFolder)
            {
                return OpenNearestParent(parent, storage, thisPc);
            }

            var child = FindChildBySegment(
                current,
                segment,
                index == segments.Count - 1 ? entry.PortableObjectId : string.Empty);
            if (child is null)
            {
                return OpenNearestParent(current, storage, thisPc);
            }

            parent = current;
            current = child;
        }

        return mode == PortableExplorerOpenMode.Reveal
            ? RevealResolved(entry, current, parent, storage, thisPc)
            : OpenResolved(entry, current, parent, storage, thisPc);
    }

    private static PortableExplorerOpenResult OpenResolved(
        FileSystemEntry entry,
        IPortableExplorerNode current,
        IPortableExplorerNode? parent,
        IPortableExplorerNode storage,
        IPortableExplorerNode thisPc)
    {
        if (entry.IsDirectory)
        {
            return current.TryOpen()
                ? PortableExplorerOpenResult.Exact()
                : OpenNearestParent(parent, storage, thisPc);
        }

        if (current.TryInvokeDefault())
        {
            return PortableExplorerOpenResult.Exact();
        }

        if (current.TrySelectInExplorer())
        {
            return PortableExplorerOpenResult.Parent("Selected the file after the default open verb failed.");
        }

        return OpenNearestParent(parent, storage, thisPc);
    }

    private static PortableExplorerOpenResult RevealResolved(
        FileSystemEntry entry,
        IPortableExplorerNode current,
        IPortableExplorerNode? parent,
        IPortableExplorerNode storage,
        IPortableExplorerNode thisPc)
    {
        if (entry.IsDirectory)
        {
            return current.TryOpen()
                ? PortableExplorerOpenResult.Exact()
                : OpenNearestParent(parent, storage, thisPc);
        }

        if (current.TrySelectInExplorer())
        {
            return PortableExplorerOpenResult.Exact();
        }

        return OpenNearestParent(parent, storage, thisPc);
    }

    private static PortableExplorerOpenResult OpenNearestParent(
        IPortableExplorerNode? nearestParent,
        IPortableExplorerNode storage,
        IPortableExplorerNode thisPc)
    {
        if (nearestParent is not null && nearestParent.TryOpen())
        {
            return ReferenceEquals(nearestParent, storage)
                ? PortableExplorerOpenResult.StorageRoot()
                : PortableExplorerOpenResult.Parent();
        }

        if (storage.TryOpen())
        {
            return PortableExplorerOpenResult.StorageRoot();
        }

        return TryOpenThisPc(thisPc);
    }

    private static PortableExplorerOpenResult TryOpenThisPc(IPortableExplorerNode thisPc)
        => thisPc.TryOpen()
            ? PortableExplorerOpenResult.ThisPc()
            : PortableExplorerOpenResult.Failed();

    private static IPortableExplorerNode? FindChild(
        IPortableExplorerNode parent,
        string expectedName,
        bool allowContains)
    {
        if (string.IsNullOrWhiteSpace(expectedName))
        {
            return null;
        }

        return parent.GetChildren().FirstOrDefault(child =>
            NamesMatch(child.Name, expectedName, allowContains));
    }

    private static IPortableExplorerNode? FindChildBySegment(
        IPortableExplorerNode parent,
        string expectedSegment,
        string expectedObjectId)
    {
        return parent.GetChildren().FirstOrDefault(child =>
            ObjectIdentityMatches(child.IdentityText, expectedObjectId) ||
            NamesMatch(child.Name, expectedSegment, allowContains: false));
    }

    private static bool NamesMatch(string shellName, string expected, bool allowContains)
    {
        var shellSegment = NormalizeSegment(shellName);
        var expectedSegment = NormalizeSegment(expected);
        if (string.IsNullOrWhiteSpace(shellSegment) || string.IsNullOrWhiteSpace(expectedSegment))
        {
            return false;
        }

        return string.Equals(shellSegment, expectedSegment, StringComparison.OrdinalIgnoreCase) ||
            (allowContains &&
                (shellSegment.Contains(expectedSegment, StringComparison.OrdinalIgnoreCase) ||
                expectedSegment.Contains(shellSegment, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool ObjectIdentityMatches(string identityText, string expectedObjectId)
    {
        if (string.IsNullOrWhiteSpace(identityText) || string.IsNullOrWhiteSpace(expectedObjectId))
        {
            return false;
        }

        var decodedIdentity = identityText;
        try
        {
            decodedIdentity = Uri.UnescapeDataString(identityText);
        }
        catch (UriFormatException)
        {
            decodedIdentity = identityText;
        }

        return decodedIdentity.Contains(expectedObjectId, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePortablePath(string value)
        => string.Join(
            '/',
            SplitPortablePath(value));

    private static IReadOnlyList<string> SplitPortablePath(string value)
        => value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeSegment)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToList();

    private static string NormalizeSegment(string value)
        => (string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim())
            .Replace('\\', '_')
            .Replace('/', '_');
}
