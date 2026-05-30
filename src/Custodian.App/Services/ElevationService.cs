using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Custodian.Core.Scanning;

namespace Custodian.App.Services;

internal static class ElevationService
{
    private const string LaunchPathArgument = "--custodian-path";

    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static void RelaunchAsAdministrator(IEnumerable<string> arguments, string? currentPath)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            using var currentProcess = Process.GetCurrentProcess();
            executablePath = currentProcess.MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Custodian could not determine the current executable path.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory
        };

        foreach (var argument in BuildRelaunchArguments(arguments, currentPath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Windows did not start the elevated Custodian process.");
        }
    }

    public static bool IsElevationCancelled(Exception ex)
        => ex is Win32Exception { NativeErrorCode: 1223 };

    public static string? GetLaunchPath(IEnumerable<string> arguments)
    {
        using var enumerator = arguments.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (!string.Equals(enumerator.Current, LaunchPathArgument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return enumerator.MoveNext() && !string.IsNullOrWhiteSpace(enumerator.Current)
                ? enumerator.Current
                : null;
        }

        return null;
    }

    public static IReadOnlyList<string> RemoveCustodianArguments(IEnumerable<string> arguments)
    {
        var filtered = new List<string>();
        using var enumerator = arguments.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (string.Equals(enumerator.Current, LaunchPathArgument, StringComparison.OrdinalIgnoreCase))
            {
                enumerator.MoveNext();
                continue;
            }

            filtered.Add(enumerator.Current);
        }

        return filtered;
    }

    private static IReadOnlyList<string> BuildRelaunchArguments(IEnumerable<string> arguments, string? currentPath)
    {
        var relaunchArguments = RemoveCustodianArguments(arguments).ToList();
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            relaunchArguments.Add(LaunchPathArgument);
            try
            {
                relaunchArguments.Add(ScanPathUtility.NormalizeRoot(currentPath));
            }
            catch
            {
                relaunchArguments.Add(currentPath);
            }
        }

        return relaunchArguments;
    }
}
