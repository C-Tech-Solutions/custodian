using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Custodian.Core.Model;
using Custodian.Core.Portable;
using Microsoft.CSharp.RuntimeBinder;

namespace Custodian.App.Services;

internal static class PortableDeviceExplorerService
{
    private const int ShellSpecialFolderMyComputer = 17;

    public static PortableExplorerOpenResult Open(
        ScanResult result,
        FileSystemEntry entry,
        PortableExplorerOpenMode mode)
    {
        object? shell = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return TryOpenThisPc()
                    ? PortableExplorerOpenResult.ThisPc()
                    : PortableExplorerOpenResult.Failed();
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return TryOpenThisPc()
                    ? PortableExplorerOpenResult.ThisPc()
                    : PortableExplorerOpenResult.Failed();
            }

            dynamic shellObject = shell;
            dynamic thisPcFolder = shellObject.NameSpace(ShellSpecialFolderMyComputer);
            if (thisPcFolder is null)
            {
                return TryOpenThisPc()
                    ? PortableExplorerOpenResult.ThisPc()
                    : PortableExplorerOpenResult.Failed();
            }

            using var thisPc = new ShellFolderNode((object)thisPcFolder, TryOpenThisPc);
            return PortableExplorerNavigator.Open(result, entry, thisPc, mode);
        }
        catch (Exception ex) when (IsShellException(ex))
        {
            return TryOpenThisPc()
                ? PortableExplorerOpenResult.ThisPc(ex.Message)
                : PortableExplorerOpenResult.Failed(ex.Message);
        }
        finally
        {
            ReleaseIfComObject(shell);
        }
    }

    public static void OpenThisPc()
    {
        Process.Start(new ProcessStartInfo("explorer.exe", "shell:MyComputerFolder") { UseShellExecute = true });
    }

    private static bool TryOpenThisPc()
    {
        try
        {
            OpenThisPc();
            return true;
        }
        catch (Exception ex) when (IsShellException(ex))
        {
            return false;
        }
    }

    private static IReadOnlyList<IPortableExplorerNode> EnumerateFolderItems(object? folder)
    {
        if (folder is null)
        {
            return [];
        }

        try
        {
            dynamic shellFolder = folder;
            object? items = null;
            var nodes = new List<IPortableExplorerNode>();
            try
            {
                items = shellFolder.Items();
                foreach (dynamic item in (dynamic)items)
                {
                    if (item is not null)
                    {
                        nodes.Add(new ShellItemNode((object)item));
                    }
                }
            }
            finally
            {
                ReleaseIfComObject(items);
            }

            return nodes;
        }
        catch (Exception ex) when (IsShellException(ex))
        {
            return [];
        }
    }

    private static bool IsShellException(Exception ex)
        => ex is COMException or RuntimeBinderException or InvalidCastException or ArgumentException or
            InvalidOperationException or Win32Exception;

    private static void ReleaseIfComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    private sealed class ShellFolderNode : IPortableExplorerNode, IDisposable
    {
        private readonly Func<bool> _openAction;
        private readonly List<IDisposable> _children = [];
        private object? _folder;

        public ShellFolderNode(object folder, Func<bool> openAction)
        {
            _folder = folder;
            _openAction = openAction;
        }

        public string Name => string.Empty;
        public string IdentityText => string.Empty;
        public bool IsFolder => true;
        public IReadOnlyList<IPortableExplorerNode> GetChildren()
        {
            var children = EnumerateFolderItems(_folder);
            TrackChildren(children, _children);
            return children;
        }

        public bool TryOpen() => _openAction();
        public bool TryInvokeDefault() => TryOpen();
        public bool TrySelectInExplorer() => TryOpen();

        public void Dispose()
        {
            DisposeChildren(_children);
            var folder = _folder;
            _folder = null;
            ReleaseIfComObject(folder);
        }
    }

    private sealed class ShellItemNode : IPortableExplorerNode, IDisposable
    {
        private readonly List<IDisposable> _children = [];
        private object? _item;

        public ShellItemNode(object item)
        {
            _item = item;
        }

        public string Name
        {
            get
            {
                try
                {
                    var item = _item;
                    if (item is null)
                    {
                        return string.Empty;
                    }

                    dynamic shellItem = item;
                    return Convert.ToString(shellItem.Name) ?? string.Empty;
                }
                catch (Exception ex) when (IsShellException(ex))
                {
                    return string.Empty;
                }
            }
        }

        public string IdentityText
        {
            get
            {
                try
                {
                    var item = _item;
                    if (item is null)
                    {
                        return string.Empty;
                    }

                    dynamic shellItem = item;
                    return Convert.ToString(shellItem.Path) ?? string.Empty;
                }
                catch (Exception ex) when (IsShellException(ex))
                {
                    return string.Empty;
                }
            }
        }

        public bool IsFolder
        {
            get
            {
                try
                {
                    var item = _item;
                    if (item is null)
                    {
                        return false;
                    }

                    dynamic shellItem = item;
                    return Convert.ToBoolean(shellItem.IsFolder);
                }
                catch (Exception ex) when (IsShellException(ex))
                {
                    return false;
                }
            }
        }

        public IReadOnlyList<IPortableExplorerNode> GetChildren()
        {
            if (!IsFolder)
            {
                return [];
            }

            try
            {
                var item = _item;
                if (item is null)
                {
                    return [];
                }

                dynamic shellItem = item;
                object? folder = null;
                try
                {
                    folder = shellItem.GetFolder;
                    var children = EnumerateFolderItems(folder);
                    TrackChildren(children, _children);
                    return children;
                }
                finally
                {
                    ReleaseIfComObject(folder);
                }
            }
            catch (Exception ex) when (IsShellException(ex))
            {
                return [];
            }
        }

        public bool TryOpen() => TryInvokeVerb();

        public bool TryInvokeDefault() => TryInvokeVerb();

        public bool TrySelectInExplorer()
        {
            var path = IdentityText;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return true;
            }
            catch (Exception ex) when (IsShellException(ex))
            {
                return false;
            }
        }

        private bool TryInvokeVerb()
        {
            try
            {
                var item = _item;
                if (item is null)
                {
                    return false;
                }

                dynamic shellItem = item;
                shellItem.InvokeVerb("open");
                return true;
            }
            catch (Exception ex) when (IsShellException(ex))
            {
                try
                {
                    var item = _item;
                    if (item is null)
                    {
                        return false;
                    }

                    dynamic shellItem = item;
                    shellItem.InvokeVerb();
                    return true;
                }
                catch (Exception fallbackEx) when (IsShellException(fallbackEx))
                {
                    return false;
                }
            }
        }

        public void Dispose()
        {
            DisposeChildren(_children);
            var item = _item;
            _item = null;
            ReleaseIfComObject(item);
        }
    }

    private static void TrackChildren(IEnumerable<IPortableExplorerNode> children, List<IDisposable> trackedChildren)
    {
        foreach (var child in children)
        {
            if (child is IDisposable disposable)
            {
                trackedChildren.Add(disposable);
            }
        }
    }

    private static void DisposeChildren(List<IDisposable> children)
    {
        foreach (var child in children)
        {
            child.Dispose();
        }

        children.Clear();
    }
}
