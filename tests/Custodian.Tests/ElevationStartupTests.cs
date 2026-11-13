using Custodian.Platform.Windows.Services;
using Microsoft.Win32;

namespace Custodian.Tests;

public sealed class ElevationStartupTests
{
    [Theory]
    [InlineData("--veloapp-install", "1.5.7")]
    [InlineData("--veloapp-updated", "1.5.7")]
    [InlineData("--veloapp-obsolete", "1.5.6")]
    [InlineData("--veloapp-uninstall", "1.5.7")]
    [InlineData("--veloapp-version")]
    [InlineData("--VELOAPP-INSTALL", "1.5.7")]
    [InlineData("--squirrel-install", "1.0.0")]
    [InlineData("--veloapp-install")]
    public void InstallerHookArgumentsAreRecognized(params string[] args)
    {
        Assert.True(ElevationService.IsInstallerHookInvocation(args));
    }

    [Theory]
    [InlineData]
    [InlineData(@"C:\")]
    [InlineData("--custodian-path", @"D:\Data")]
    [InlineData("--scan", @"C:\", "--veloapp-install")]
    public void NormalLaunchArgumentsAreNotInstallerHooks(params string[] args)
    {
        Assert.False(ElevationService.IsInstallerHookInvocation(args));
    }

    [Fact]
    public void StartupRelaunchesElevatedWhenSettingIsOnAndProcessIsNotElevated()
    {
        Assert.True(ElevationService.ShouldRelaunchElevatedAtStartup([@"C:\"], settingEnabled: true, isElevated: false));
        Assert.True(ElevationService.ShouldRelaunchElevatedAtStartup([], settingEnabled: true, isElevated: false));
    }

    [Theory]
    [InlineData("--veloapp-install", "1.5.7")]
    [InlineData("--veloapp-updated", "1.5.7")]
    [InlineData("--veloapp-obsolete", "1.5.6")]
    [InlineData("--veloapp-uninstall", "1.5.7")]
    public void InstallerHooksNeverRelaunchElevated(params string[] args)
    {
        Assert.False(ElevationService.ShouldRelaunchElevatedAtStartup(args, settingEnabled: true, isElevated: false));
    }

    [Fact]
    public void StartupDoesNotRelaunchWhenSettingIsOffOrAlreadyElevated()
    {
        Assert.False(ElevationService.ShouldRelaunchElevatedAtStartup([], settingEnabled: false, isElevated: false));
        Assert.False(ElevationService.ShouldRelaunchElevatedAtStartup([], settingEnabled: true, isElevated: true));
    }

    [Fact]
    public void StartupElevationRelaunchNeverRelaunchesAgain()
    {
        var relaunchArguments = ElevationService.BuildRelaunchArguments([@"C:\"], currentPath: null, startupElevation: true);

        Assert.False(ElevationService.ShouldRelaunchElevatedAtStartup(relaunchArguments, settingEnabled: true, isElevated: false));
    }

    [Fact]
    public void StartupRelaunchArgumentsCarryOneMarkerAndTheLaunchPath()
    {
        var relaunchArguments = ElevationService.BuildRelaunchArguments(
            ["--mode", "mft", ElevationService.StartupElevationArgument, "--custodian-path", @"E:\Old"],
            @"D:\Data",
            startupElevation: true);

        Assert.Equal(
            ["--mode", "mft", ElevationService.StartupElevationArgument, "--custodian-path", @"D:\Data"],
            relaunchArguments);
    }

    [Fact]
    public void ManualRelaunchArgumentsDoNotCarryTheStartupMarker()
    {
        var relaunchArguments = ElevationService.BuildRelaunchArguments(
            [ElevationService.StartupElevationArgument, @"C:\"],
            currentPath: null,
            startupElevation: false);

        Assert.Equal([@"C:\"], relaunchArguments);
    }

    [Fact]
    public void CustodianArgumentsAreRemovedBeforeVelopackAndTuiParsing()
    {
        var filtered = ElevationService.RemoveCustodianArguments(
            ["--veloapp-install", "1.5.7", ElevationService.StartupElevationArgument, "--custodian-path", @"C:\"]);

        Assert.Equal(["--veloapp-install", "1.5.7"], filtered);
    }

    [Theory]
    [InlineData("~ RUNASADMIN", null)]
    [InlineData("~ runasadmin", null)]
    [InlineData("RUNASADMIN", null)]
    [InlineData("~ RUNASADMIN HIGHDPIAWARE", "~ HIGHDPIAWARE")]
    [InlineData("~ HIGHDPIAWARE", "~ HIGHDPIAWARE")]
    public void RemovingTheRunAsAdministratorLayerKeepsOtherLayers(string layers, string? expected)
    {
        Assert.Equal(expected, RunAsAdministratorLaunchSetting.RemoveRunAsAdministratorLayer(layers));
    }

    [Fact]
    public void MigrationMovesLegacyLayerIntoCustodianSetting()
    {
        using var registry = new TemporaryRegistryRoot();
        const string exe = @"C:\Users\Owner\AppData\Local\Custodian.DiskAnalyzer\current\Custodian.App.exe";
        registry.SetLayers(exe, "~ RUNASADMIN");
        var setting = new RunAsAdministratorLaunchSetting(registry.Root, exe);

        Assert.True(setting.MigrateLegacyCompatibilityLayer());

        Assert.True(setting.IsEnabled());
        Assert.Null(registry.GetLayers(exe));
    }

    [Fact]
    public void MigrationKeepsUnrelatedCompatibilityLayers()
    {
        using var registry = new TemporaryRegistryRoot();
        const string exe = @"C:\Custodian\Custodian.App.exe";
        registry.SetLayers(exe, "~ RUNASADMIN HIGHDPIAWARE");
        var setting = new RunAsAdministratorLaunchSetting(registry.Root, exe);

        Assert.True(setting.MigrateLegacyCompatibilityLayer());

        Assert.True(setting.IsEnabled());
        Assert.Equal("~ HIGHDPIAWARE", registry.GetLayers(exe));
    }

    [Fact]
    public void MigrationLeavesOtherExecutablesAlone()
    {
        using var registry = new TemporaryRegistryRoot();
        const string exe = @"C:\Custodian\Custodian.App.exe";
        const string otherExe = @"C:\Other\Other.exe";
        registry.SetLayers(otherExe, "~ RUNASADMIN");
        var setting = new RunAsAdministratorLaunchSetting(registry.Root, exe);

        Assert.False(setting.MigrateLegacyCompatibilityLayer());

        Assert.False(setting.IsEnabled());
        Assert.Equal("~ RUNASADMIN", registry.GetLayers(otherExe));
    }

    [Fact]
    public void MigrationWithoutLegacyLayerDoesNotEnableSetting()
    {
        using var registry = new TemporaryRegistryRoot();
        const string exe = @"C:\Custodian\Custodian.App.exe";
        var setting = new RunAsAdministratorLaunchSetting(registry.Root, exe);

        Assert.False(setting.MigrateLegacyCompatibilityLayer());
        Assert.False(setting.IsEnabled());
    }

    [Fact]
    public void EnablingTheSettingNeverWritesACompatibilityLayer()
    {
        using var registry = new TemporaryRegistryRoot();
        const string exe = @"C:\Custodian\Custodian.App.exe";
        var setting = new RunAsAdministratorLaunchSetting(registry.Root, exe);

        setting.SetEnabled(true);

        Assert.True(setting.IsEnabled());
        Assert.Null(registry.GetLayers(exe));
    }

    [Fact]
    public void DisablingTheSettingAlsoClearsALegacyLayer()
    {
        using var registry = new TemporaryRegistryRoot();
        const string exe = @"C:\Custodian\Custodian.App.exe";
        var setting = new RunAsAdministratorLaunchSetting(registry.Root, exe);
        setting.SetEnabled(true);
        registry.SetLayers(exe, "~ RUNASADMIN");

        setting.SetEnabled(false);

        Assert.False(setting.IsEnabled());
        Assert.Null(registry.GetLayers(exe));
        Assert.False(setting.MigrateLegacyCompatibilityLayer());
    }

    [Fact]
    public void SettingIsKeptPerExecutable()
    {
        using var registry = new TemporaryRegistryRoot();
        var app = new RunAsAdministratorLaunchSetting(registry.Root, @"C:\Custodian\Custodian.App.exe");
        var tui = new RunAsAdministratorLaunchSetting(registry.Root, @"C:\Custodian\tui\Custodian.Tui.exe");

        app.SetEnabled(true);

        Assert.True(app.IsEnabled());
        Assert.False(tui.IsEnabled());
    }

    /// <summary>
    /// A throwaway HKCU subtree that stands in for HKCU, so tests never touch the real
    /// Custodian setting or Windows compatibility layers.
    /// </summary>
    private sealed class TemporaryRegistryRoot : IDisposable
    {
        private readonly string _subKeyPath = $@"Software\Custodian.Tests.{Guid.NewGuid():N}";

        public TemporaryRegistryRoot()
        {
            Root = Registry.CurrentUser.CreateSubKey(_subKeyPath, writable: true);
        }

        public RegistryKey Root { get; }

        public void SetLayers(string executablePath, string layers)
        {
            using var key = Root.CreateSubKey(RunAsAdministratorLaunchSetting.CompatibilityLayersSubKey, writable: true);
            key.SetValue(executablePath, layers, RegistryValueKind.String);
        }

        public string? GetLayers(string executablePath)
        {
            using var key = Root.OpenSubKey(RunAsAdministratorLaunchSetting.CompatibilityLayersSubKey, writable: false);
            return key?.GetValue(executablePath) as string;
        }

        public void Dispose()
        {
            Root.Dispose();
            Registry.CurrentUser.DeleteSubKeyTree(_subKeyPath, throwOnMissingSubKey: false);
        }
    }
}
