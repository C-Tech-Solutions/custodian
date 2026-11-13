using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Custodian.Core.Scanning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Custodian.Platform.Windows.Services;

internal static class ElevationService
{
    private const string LaunchPathArgument = "--custodian-path";

    // Marks the copy started by the startup self-elevation, so it never tries again.
    // Without it, a "runas" that comes back non-elevated (for example with UAC turned
    // off for a standard user) would relaunch forever.
    internal const string StartupElevationArgument = "--custodian-startup-elevation";

    // Velopack runs the main exe with one of these as the first argument for its install,
    // update, obsolete, uninstall and version hooks. A hook must never elevate or show a
    // UAC prompt.
    private static readonly string[] InstallerHookArgumentPrefixes = ["--veloapp-", "--squirrel-"];

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
        => RelaunchAsAdministrator(arguments, currentPath, startupElevation: false);

    private static void RelaunchAsAdministrator(IEnumerable<string> arguments, string? currentPath, bool startupElevation)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = CurrentExecutablePath(),
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = TrustedRelaunchWorkingDirectory(),
            Arguments = string.Join(" ", BuildRelaunchArguments(arguments, currentPath, startupElevation).Select(EscapeCommandLineArgument))
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Windows did not start the elevated Custodian process.");
        }
    }

    public static bool IsRunAsAdministratorOnLaunchEnabled()
        => CurrentLaunchSetting().IsEnabled();

    public static void SetRunAsAdministratorOnLaunch(bool enabled)
        => CurrentLaunchSetting().SetEnabled(enabled);

    /// <summary>
    /// Runs once per normal launch, after Velopack has handled its hook arguments and
    /// before any UI is shown. Converts a legacy compatibility layer into the Custodian
    /// setting and, when the setting is on and the process is not elevated, starts an
    /// elevated copy.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when an elevated copy was started and this process should
    /// exit without showing UI; <see langword="false"/> to continue starting normally.
    /// </returns>
    public static bool TryRelaunchElevatedAtStartup(IReadOnlyList<string> arguments, string? launchPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(logger);

        if (IsInstallerHookInvocation(arguments))
        {
            return false;
        }

        bool enabled;
        try
        {
            var setting = CurrentLaunchSetting();
            if (setting.MigrateLegacyCompatibilityLayer())
            {
                logger.LogInformation("Moved the administrator launch setting off the Windows compatibility layer.");
            }

            enabled = setting.IsEnabled();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read the administrator launch setting; starting without elevation.");
            return false;
        }

        var isElevated = IsRunningAsAdministrator();
        if (!ShouldRelaunchElevatedAtStartup(arguments, enabled, isElevated))
        {
            if (enabled && !isElevated && IsStartupElevationRelaunch(arguments))
            {
                logger.LogWarning("Custodian is still not elevated after the startup elevation relaunch; continuing without elevation.");
            }

            return false;
        }

        try
        {
            RelaunchAsAdministrator(arguments, launchPath, startupElevation: true);
            logger.LogInformation("Started an elevated Custodian because Always open as administrator is on.");
            return true;
        }
        catch (Exception ex) when (IsElevationCancelled(ex))
        {
            logger.LogInformation("Administrator prompt was declined at startup; continuing without elevation.");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to start an elevated Custodian at startup; continuing without elevation.");
            return false;
        }
    }

    internal static bool ShouldRelaunchElevatedAtStartup(IReadOnlyList<string> arguments, bool settingEnabled, bool isElevated)
        => settingEnabled
            && !isElevated
            && !IsInstallerHookInvocation(arguments)
            && !IsStartupElevationRelaunch(arguments);

    /// <summary>
    /// True when Velopack (or legacy Squirrel) started this process to run one of its hooks.
    /// </summary>
    internal static bool IsInstallerHookInvocation(IReadOnlyList<string> arguments)
        => arguments.Count > 0
            && InstallerHookArgumentPrefixes.Any(prefix => arguments[0].StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool IsStartupElevationRelaunch(IEnumerable<string> arguments)
        => arguments.Any(argument => string.Equals(argument, StartupElevationArgument, StringComparison.OrdinalIgnoreCase));

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

            if (string.Equals(enumerator.Current, StartupElevationArgument, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            filtered.Add(enumerator.Current);
        }

        return filtered;
    }

    internal static IReadOnlyList<string> BuildRelaunchArguments(IEnumerable<string> arguments, string? currentPath, bool startupElevation)
    {
        var relaunchArguments = RemoveCustodianArguments(arguments).ToList();
        if (startupElevation)
        {
            relaunchArguments.Add(StartupElevationArgument);
        }

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

    private static RunAsAdministratorLaunchSetting CurrentLaunchSetting()
        => new(Registry.CurrentUser, CurrentExecutablePath());

    internal static string TrustedRelaunchWorkingDirectory() => WindowsShellLauncher.TrustedWorkingDirectory();

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
