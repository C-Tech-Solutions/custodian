using Custodian.Platform.Windows.Logging;
using Custodian.Platform.Windows.Services;

namespace Custodian.Tests;

public sealed class LaunchSecurityTests
{
    [Theory]
    [InlineData(@"C:\Users\Owner\AppData\Local\Temp\payload.exe")]
    [InlineData(@"C:\Users\Owner\AppData\Local\Temp\script.ps1")]
    [InlineData(@"C:\Users\Owner\AppData\Local\Temp\shortcut.lnk")]
    [InlineData(@"C:\Users\Owner\AppData\Local\Temp\install.msi")]
    public void LoadedScanExecutableOrScriptPathRequiresOpenConfirmation(string path)
    {
        var reason = FileLaunchSafety.OpenConfirmationReason(path, loadedFromScanFile: true);

        Assert.Equal(FileLaunchConfirmationReason.LoadedScanExecutableOrScript, reason);
    }

    [Fact]
    public void LoadedScanTextPathDoesNotRequireExecutableConfirmation()
    {
        var reason = FileLaunchSafety.OpenConfirmationReason(
            @"C:\Users\Owner\Documents\notes.txt",
            loadedFromScanFile: true);

        Assert.Equal(FileLaunchConfirmationReason.None, reason);
    }

    [Fact]
    public void RemotePathRequiresOpenConfirmationEvenWhenScanWasNotLoaded()
    {
        var reason = FileLaunchSafety.OpenConfirmationReason(
            @"\\server\share\payload.txt",
            loadedFromScanFile: false);

        Assert.Equal(FileLaunchConfirmationReason.RemotePath, reason);
    }

    [Fact]
    public void RevealFromLoadedScanRequiresConfirmationForExecutableOrScriptPath()
    {
        var reason = FileLaunchSafety.RevealConfirmationReason(
            @"C:\Users\Owner\AppData\Local\Temp\payload.scr",
            loadedFromScanFile: true);

        Assert.Equal(FileLaunchConfirmationReason.LoadedScanExecutableOrScript, reason);
    }

    [Fact]
    public void ExplorerLaunchesUseFullyQualifiedWindowsExplorer()
    {
        var startInfo = WindowsShellLauncher.CreateRevealStartInfo(@"C:\Temp\file.txt");

        Assert.True(Path.IsPathFullyQualified(startInfo.FileName));
        Assert.Equal("explorer.exe", Path.GetFileName(startInfo.FileName), ignoreCase: true);
        Assert.True(startInfo.UseShellExecute);
        Assert.Contains(startInfo.ArgumentList, argument => argument == @"/select,C:\Temp\file.txt");
    }

    [Fact]
    public void ElevatedRelaunchUsesTrustedWorkingDirectoryInsteadOfInheritedCurrentDirectory()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var tempDirectory = Directory.CreateTempSubdirectory("custodian-elevation-cwd-").FullName;
        try
        {
            Environment.CurrentDirectory = tempDirectory;

            var trustedDirectory = ElevationService.TrustedRelaunchWorkingDirectory();

            Assert.True(Path.IsPathFullyQualified(trustedDirectory));
            Assert.True(Directory.Exists(trustedDirectory));
            Assert.NotEqual(
                NormalizeDirectory(tempDirectory),
                NormalizeDirectory(trustedDirectory),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void FileLoggerEscapesNewlinesTabsAndControlCharacters()
    {
        var sanitized = FileLoggerProvider.SanitizeLogSegment("first\r\nsecond\t\u0001end");

        Assert.Equal(@"first\r\nsecond\t\u0001end", sanitized);
        Assert.DoesNotContain('\r', sanitized);
        Assert.DoesNotContain('\n', sanitized);
        Assert.DoesNotContain('\t', sanitized);
        Assert.DoesNotContain('\u0001', sanitized);
    }

    private static string NormalizeDirectory(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
