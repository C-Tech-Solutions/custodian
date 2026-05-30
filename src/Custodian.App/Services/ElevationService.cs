using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Custodian.App.Services;

internal static class ElevationService
{
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

    public static Process RelaunchAsAdministrator(IEnumerable<string> arguments)
    {
        var executablePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Custodian could not determine the current executable path.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory,
            Arguments = JoinArguments(arguments)
        };

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the elevated Custodian process.");
    }

    public static bool IsElevationCancelled(Exception ex)
        => ex is Win32Exception { NativeErrorCode: 1223 };

    private static string JoinArguments(IEnumerable<string> arguments)
        => string.Join(" ", arguments.Select(QuoteArgument));

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        if (!argument.Any(char.IsWhiteSpace) && !argument.Contains('"'))
        {
            return argument;
        }

        var builder = new System.Text.StringBuilder();
        builder.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            builder.Append(character);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }
}
