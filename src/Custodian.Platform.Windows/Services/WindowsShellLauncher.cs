using System.Diagnostics;

namespace Custodian.Platform.Windows.Services;

internal static class WindowsShellLauncher
{
    public static string ExplorerPath()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windowsDirectory))
        {
            return Path.Combine(windowsDirectory, "explorer.exe");
        }

        return Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "..", "explorer.exe"));
    }

    public static ProcessStartInfo CreateExplorerStartInfo(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(ExplorerPath())
        {
            UseShellExecute = true,
            WorkingDirectory = TrustedWorkingDirectory()
        };

        foreach (var argument in arguments.Where(argument => !string.IsNullOrWhiteSpace(argument)))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    public static ProcessStartInfo CreateRevealStartInfo(string path)
        => CreateExplorerStartInfo($"/select,{path}");

    public static ProcessStartInfo CreateOpenDirectoryStartInfo(string path)
        => CreateExplorerStartInfo(path);

    public static string TrustedWorkingDirectory()
    {
        var appDirectory = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(appDirectory) && Directory.Exists(appDirectory))
        {
            return appDirectory;
        }

        var systemDirectory = Environment.SystemDirectory;
        return !string.IsNullOrWhiteSpace(systemDirectory) && Directory.Exists(systemDirectory)
            ? systemDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    }
}
