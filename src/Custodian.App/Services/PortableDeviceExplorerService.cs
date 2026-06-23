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

            var thisPc = new ShellFolderNode((object)thisPcFolder, TryOpenThisPc);
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
            var nodes = new List<IPortableExplorerNode>();
            foreach (dynamic item in shellFolder.Items())
            {
                nodes.Add(new ShellItemNode((object)item));
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

    private sealed class ShellFolderNode(object folder, Func<bool> openAction) : IPortableExplorerNode
    {
        public string Name => string.Empty;
        public string IdentityText => string.Empty;
        public bool IsFolder => true;
        public IReadOnlyList<IPortableExplorerNode> GetChildren() => EnumerateFolderItems(folder);
        public bool TryOpen() => openAction();
        public bool TryInvokeDefault() => TryOpen();
        public bool TrySelectInExplorer() => TryOpen();
    }

    private sealed class ShellItemNode(object item) : IPortableExplorerNode
    {
        public string Name
        {
            get
            {
                try
                {
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
                dynamic shellItem = item;
                return EnumerateFolderItems((object)shellItem.GetFolder);
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
                dynamic shellItem = item;
                shellItem.InvokeVerb("open");
                return true;
            }
            catch (Exception ex) when (IsShellException(ex))
            {
                try
                {
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
    }
}
