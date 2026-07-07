using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Custodian.Core.Scanning;
using Microsoft.Win32;

namespace Custodian.Platform.Windows.Services;

internal static class ElevationService
{
    private const string LaunchPathArgument = "--custodian-path";
    private const string CompatibilityLayersSubKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const string RunAsAdministratorLayer = "RUNASADMIN";
    private const string CompatibilityLayerPrefix = "~";

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
        var startInfo = new ProcessStartInfo
        {
            FileName = CurrentExecutablePath(),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = TrustedRelaunchWorkingDirectory(),
            Arguments = string.Join(" ", BuildRelaunchArguments(arguments, currentPath).Select(EscapeCommandLineArgument))
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Windows did not start the elevated Custodian process.");
        }
    }

    public static bool IsRunAsAdministratorOnLaunchEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(CompatibilityLayersSubKey, writable: false);
        var layers = key?.GetValue(CurrentExecutablePath()) as string;
        return HasRunAsAdministratorLayer(layers);
    }

    public static void SetRunAsAdministratorOnLaunch(bool enabled)
    {
        var executablePath = CurrentExecutablePath();
        using var key = Registry.CurrentUser.CreateSubKey(CompatibilityLayersSubKey, writable: true)
            ?? throw new InvalidOperationException("Custodian could not open Windows compatibility settings.");

        var existing = key.GetValue(executablePath) as string;
        var updated = enabled
            ? AddRunAsAdministratorLayer(existing)
            : RemoveRunAsAdministratorLayer(existing);

        if (string.IsNullOrWhiteSpace(updated))
        {
            key.DeleteValue(executablePath, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(executablePath, updated, RegistryValueKind.String);
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

    private static string CurrentExecutablePath()
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

        return executablePath;
    }

    internal static string TrustedRelaunchWorkingDirectory() => WindowsShellLauncher.TrustedWorkingDirectory();

    private static bool HasRunAsAdministratorLayer(string? layers)
        => LayerTokens(layers).Any(token => string.Equals(token, RunAsAdministratorLayer, StringComparison.OrdinalIgnoreCase));

    private static string AddRunAsAdministratorLayer(string? layers)
    {
        var tokens = LayerTokens(layers).ToList();
        if (!tokens.Any(token => string.Equals(token, CompatibilityLayerPrefix, StringComparison.Ordinal)))
        {
            tokens.Insert(0, CompatibilityLayerPrefix);
        }

        if (!tokens.Any(token => string.Equals(token, RunAsAdministratorLayer, StringComparison.OrdinalIgnoreCase)))
        {
            tokens.Add(RunAsAdministratorLayer);
        }

        return string.Join(' ', tokens);
    }

    private static string? RemoveRunAsAdministratorLayer(string? layers)
    {
        var tokens = LayerTokens(layers)
            .Where(token => !string.Equals(token, RunAsAdministratorLayer, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (tokens.Count == 0 ||
            tokens.All(token => string.Equals(token, CompatibilityLayerPrefix, StringComparison.Ordinal)))
        {
            return null;
        }

        return string.Join(' ', tokens);
    }

    private static IEnumerable<string> LayerTokens(string? layers)
        => (layers ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string EscapeCommandLineArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        if (!argument.Any(character => char.IsWhiteSpace(character) || character is '"' or '\\'))
        {
            return argument;
        }

        var builder = new System.Text.StringBuilder();
        builder.Append('"');
        for (var index = 0; index < argument.Length; index++)
        {
            var backslashes = 0;
            while (index < argument.Length && argument[index] == '\\')
            {
                backslashes++;
                index++;
            }

            if (index == argument.Length)
            {
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (argument[index] == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', backslashes);
                builder.Append(argument[index]);
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
