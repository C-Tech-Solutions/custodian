using Custodian.Platform.Windows.Services;

namespace Custodian.Tests;

public sealed class CloudProviderDiscoveryServiceTests
{
    [Fact]
    public void GetTargetsUsesRegistryAccountAndKnownFolderDetails()
    {
        var env = new FakeCloudProviderEnvironment();
        env.AccountCandidates.Add(new OneDriveRootCandidate(@"C:\Users\Me\OneDrive - Work", "Work Tenant"));
        env.ExistingDirectories.Add(@"C:\Users\Me\OneDrive - Work");
        env.KnownFolders["Desktop"] = @"C:\Users\Me\OneDrive - Work\Desktop";
        env.KnownFolders["Documents"] = @"C:\Users\Me\Documents";
        var service = new CloudProviderDiscoveryService(env);

        var target = Assert.Single(service.GetTargets());

        Assert.Equal("onedrive", target.ProviderId);
        Assert.Equal("OneDrive", target.ProviderName);
        Assert.Equal("Work Tenant", target.AccountLabel);
        Assert.Equal(@"C:\Users\Me\OneDrive - Work", target.RootPath);
        Assert.Contains("Desktop", target.KnownFolderNames);
        Assert.DoesNotContain("Documents", target.KnownFolderNames);
        Assert.Contains("Work Tenant", target.DetailText);
    }

    [Fact]
    public void GetTargetsDeduplicatesRootsAndSkipsMissingFolders()
    {
        var env = new FakeCloudProviderEnvironment();
        env.AccountCandidates.Add(new OneDriveRootCandidate(@"C:\Users\Me\OneDrive", "Personal"));
        env.EnvironmentCandidates.Add(new OneDriveRootCandidate(@"C:\Users\Me\OneDrive\", "Personal"));
        env.EnvironmentCandidates.Add(new OneDriveRootCandidate(@"C:\Users\Me\Missing", "Business"));
        env.ExistingDirectories.Add(@"C:\Users\Me\OneDrive");
        var service = new CloudProviderDiscoveryService(env);

        var target = Assert.Single(service.GetTargets());

        Assert.Equal(@"C:\Users\Me\OneDrive", target.RootPath);
        Assert.Equal("Personal", target.AccountLabel);
    }

    [Fact]
    public void GetTargetsFallsBackToUserProfileOneDrive()
    {
        var env = new FakeCloudProviderEnvironment
        {
            UserProfilePath = @"C:\Users\Me"
        };
        env.ExistingDirectories.Add(@"C:\Users\Me\OneDrive");
        var service = new CloudProviderDiscoveryService(env);

        var target = Assert.Single(service.GetTargets());

        Assert.Equal(@"C:\Users\Me\OneDrive", target.RootPath);
        Assert.Equal("Personal", target.AccountLabel);
    }

    [Fact]
    public void GetTargetsIncludesNextcloudRootFromConfig()
    {
        var env = new FakeCloudProviderEnvironment();
        env.ConfigurationFiles.Add(new NextcloudConfigurationFile(
            @"C:\Users\Me\AppData\Roaming\Nextcloud\nextcloud.cfg",
            """
            [Accounts]
            0\url=https://cloud.example.test
            0\FoldersWithPlaceholders\1\localPath=C:/Users/Me/Nextcloud/
            0\FoldersWithPlaceholders\1\targetPath=/
            """));
        env.ExistingDirectories.Add(@"C:\Users\Me\Nextcloud");
        var service = new CloudProviderDiscoveryService(env);

        var target = Assert.Single(service.GetTargets());

        Assert.Equal("nextcloud", target.ProviderId);
        Assert.Equal("Nextcloud", target.ProviderName);
        Assert.Equal("cloud.example.test", target.AccountLabel);
        Assert.Equal(@"C:\Users\Me\Nextcloud", target.RootPath);
        Assert.Contains("cloud.example.test", target.DetailText);
    }

    [Fact]
    public void GetTargetsSupportsNextcloudFoldersAndFoldersWithPlaceholders()
    {
        var env = new FakeCloudProviderEnvironment();
        env.ConfigurationFiles.Add(new NextcloudConfigurationFile(
            @"C:\Users\Me\AppData\Roaming\Nextcloud\nextcloud.cfg",
            """
            [Accounts]
            0\url=https://cloud.example.test
            0\FoldersWithPlaceholders\1\localPath=C:/Users/Me/Nextcloud/
            [Accounts\0\Folders\2]
            localPath=C:/Users/Me/Nextcloud Archive/
            """));
        env.ExistingDirectories.Add(@"C:\Users\Me\Nextcloud");
        env.ExistingDirectories.Add(@"C:\Users\Me\Nextcloud Archive");
        var service = new CloudProviderDiscoveryService(env);

        var targets = service.GetTargets();

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, target => target.RootPath == @"C:\Users\Me\Nextcloud");
        Assert.Contains(targets, target => target.RootPath == @"C:\Users\Me\Nextcloud Archive");
        Assert.All(targets, target => Assert.Equal("nextcloud", target.ProviderId));
    }

    [Fact]
    public void GetTargetsSkipsMissingNextcloudRootDirectory()
    {
        var env = new FakeCloudProviderEnvironment();
        env.ConfigurationFiles.Add(new NextcloudConfigurationFile(
            @"C:\Users\Me\AppData\Roaming\Nextcloud\nextcloud.cfg",
            """
            [Accounts]
            0\url=https://cloud.example.test
            0\FoldersWithPlaceholders\1\localPath=C:/Users/Me/MissingNextcloud/
            """));
        var service = new CloudProviderDiscoveryService(env);

        Assert.Empty(service.GetTargets());
    }

    [Fact]
    public void GetTargetsHandlesMalformedNextcloudConfigGracefully()
    {
        var env = new FakeCloudProviderEnvironment();
        env.ConfigurationFiles.Add(new NextcloudConfigurationFile(
            @"C:\Users\Me\AppData\Roaming\Nextcloud\nextcloud.cfg",
            """
            [Accounts]
            malformed
            0\FoldersWithPlaceholders\1\localPath=
            0\FoldersWithPlaceholders\2\localPath=\\server
            """));
        env.AccountCandidates.Add(new OneDriveRootCandidate(@"C:\Users\Me\OneDrive", "Personal"));
        env.ExistingDirectories.Add(@"C:\Users\Me\OneDrive");
        var service = new CloudProviderDiscoveryService(env);

        var target = Assert.Single(service.GetTargets());

        Assert.Equal("onedrive", target.ProviderId);
        Assert.Equal(@"C:\Users\Me\OneDrive", target.RootPath);
    }

    [Fact]
    public void GetTargetsUsesConfiguredNextcloudRootsInsteadOfProfileFallbacks()
    {
        var env = new FakeCloudProviderEnvironment();
        env.ConfigurationFiles.Add(new NextcloudConfigurationFile(
            @"C:\Users\Me\AppData\Roaming\Nextcloud\nextcloud.cfg",
            """
            [Accounts]
            0\url=https://cloud.example.test
            0\FoldersWithPlaceholders\1\localPath=C:/Users/Me/Nextcloud3/
            """));
        env.NextcloudProfileCandidates.Add(@"C:\Users\Me\Nextcloud");
        env.NextcloudProfileCandidates.Add(@"C:\Users\Me\Nextcloud2");
        env.NextcloudProfileCandidates.Add(@"C:\Users\Me\Nextcloud3");
        env.ExistingDirectories.Add(@"C:\Users\Me\Nextcloud");
        env.ExistingDirectories.Add(@"C:\Users\Me\Nextcloud2");
        env.ExistingDirectories.Add(@"C:\Users\Me\Nextcloud3");
        var service = new CloudProviderDiscoveryService(env);

        var target = Assert.Single(service.GetTargets());

        Assert.Equal("nextcloud", target.ProviderId);
        Assert.Equal("cloud.example.test", target.AccountLabel);
        Assert.Equal(@"C:\Users\Me\Nextcloud3", target.RootPath);
    }

    [Fact]
    public void GetTargetsFallsBackToNextcloudProfileCandidateWhenConfigHasNoValidRoot()
    {
        var env = new FakeCloudProviderEnvironment();
        env.ConfigurationFiles.Add(new NextcloudConfigurationFile(
            @"C:\Users\Me\AppData\Roaming\Nextcloud\nextcloud.cfg",
            """
            [Accounts]
            0\url=https://cloud.example.test
            0\FoldersWithPlaceholders\1\localPath=C:/Users/Me/MissingNextcloud/
            """));
        env.NextcloudProfileCandidates.Add(@"C:\Users\Me\Nextcloud - Work");
        env.ExistingDirectories.Add(@"C:\Users\Me\Nextcloud - Work");
        var service = new CloudProviderDiscoveryService(env);

        var target = Assert.Single(service.GetTargets());

        Assert.Equal("nextcloud", target.ProviderId);
        Assert.Equal("Nextcloud", target.ProviderName);
        Assert.Equal(string.Empty, target.AccountLabel);
        Assert.Equal(@"C:\Users\Me\Nextcloud - Work", target.RootPath);
    }

    [Fact]
    public void GetTargetsIncludesDropboxPersonalAndBusinessRootsFromConfig()
    {
        var env = new FakeCloudProviderEnvironment();
        env.DropboxConfigurationFiles.Add(new DropboxConfigurationFile(
            @"C:\Users\Me\AppData\Roaming\Dropbox\info.json",
            """
            {
              "personal": { "path": "C:\\Users\\Me\\Dropbox" },
              "business": { "path": "C:\\Users\\Me\\Dropbox (Acme)" }
            }
            """));
        env.ExistingDirectories.Add(@"C:\Users\Me\Dropbox");
        env.ExistingDirectories.Add(@"C:\Users\Me\Dropbox (Acme)");
        var service = new CloudProviderDiscoveryService(env);

        var targets = service.GetTargets();

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, target =>
            target.ProviderId == "dropbox" &&
            target.ProviderName == "Dropbox" &&
            target.AccountLabel == "Personal" &&
            target.RootPath == @"C:\Users\Me\Dropbox");
        Assert.Contains(targets, target =>
            target.ProviderId == "dropbox" &&
            target.ProviderName == "Dropbox" &&
            target.AccountLabel == "Business" &&
            target.RootPath == @"C:\Users\Me\Dropbox (Acme)");
    }

    [Fact]
    public void GetTargetsSkipsMissingDropboxConfiguredDirectories()
    {
        var env = new FakeCloudProviderEnvironment();
        env.DropboxConfigurationFiles.Add(new DropboxConfigurationFile(
            @"C:\Users\Me\AppData\Roaming\Dropbox\info.json",
            """{ "personal": { "path": "C:\\Users\\Me\\MissingDropbox" } }"""));
        var service = new CloudProviderDiscoveryService(env);

        Assert.Empty(service.GetTargets());
    }

    [Fact]
    public void GetTargetsHandlesMalformedDropboxConfigGracefully()
    {
        var env = new FakeCloudProviderEnvironment();
        env.DropboxConfigurationFiles.Add(new DropboxConfigurationFile(
            @"C:\Users\Me\AppData\Roaming\Dropbox\info.json",
            """{ "personal": { "path": "" }"""));
        env.AccountCandidates.Add(new OneDriveRootCandidate(@"C:\Users\Me\OneDrive", "Personal"));
        env.ExistingDirectories.Add(@"C:\Users\Me\OneDrive");
        var service = new CloudProviderDiscoveryService(env);

        var target = Assert.Single(service.GetTargets());

        Assert.Equal("onedrive", target.ProviderId);
        Assert.Equal(@"C:\Users\Me\OneDrive", target.RootPath);
    }

    [Fact]
    public void GetTargetsUsesConfiguredDropboxRootsInsteadOfProfileFallbacks()
    {
        var env = new FakeCloudProviderEnvironment();
        env.DropboxConfigurationFiles.Add(new DropboxConfigurationFile(
            @"C:\Users\Me\AppData\Local\Dropbox\info.json",
            """{ "business": { "path": "C:\\Users\\Me\\Dropbox (Acme)" } }"""));
        env.DropboxProfileCandidates.Add(@"C:\Users\Me\Dropbox");
        env.DropboxProfileCandidates.Add(@"C:\Users\Me\Dropbox Old");
        env.ExistingDirectories.Add(@"C:\Users\Me\Dropbox");
        env.ExistingDirectories.Add(@"C:\Users\Me\Dropbox Old");
        env.ExistingDirectories.Add(@"C:\Users\Me\Dropbox (Acme)");
        var service = new CloudProviderDiscoveryService(env);

        var target = Assert.Single(service.GetTargets());

        Assert.Equal("dropbox", target.ProviderId);
        Assert.Equal("Business", target.AccountLabel);
        Assert.Equal(@"C:\Users\Me\Dropbox (Acme)", target.RootPath);
    }

    [Fact]
    public void GetTargetsFallsBackToDropboxProfileCandidateWhenConfigHasNoValidRoot()
    {
        var env = new FakeCloudProviderEnvironment();
        env.DropboxConfigurationFiles.Add(new DropboxConfigurationFile(
            @"C:\Users\Me\AppData\Roaming\Dropbox\info.json",
            """{ "personal": { "path": "C:\\Users\\Me\\MissingDropbox" } }"""));
        env.DropboxProfileCandidates.Add(@"C:\Users\Me\Dropbox - Work");
        env.ExistingDirectories.Add(@"C:\Users\Me\Dropbox - Work");
        var service = new CloudProviderDiscoveryService(env);

        var target = Assert.Single(service.GetTargets());

        Assert.Equal("dropbox", target.ProviderId);
        Assert.Equal("Dropbox", target.ProviderName);
        Assert.Equal(string.Empty, target.AccountLabel);
        Assert.Equal(@"C:\Users\Me\Dropbox - Work", target.RootPath);
    }

    [Fact]
    public void GetTargetsDeduplicatesDropboxConfiguredRoots()
    {
        var env = new FakeCloudProviderEnvironment();
        env.DropboxConfigurationFiles.Add(new DropboxConfigurationFile(
            @"C:\Users\Me\AppData\Roaming\Dropbox\info.json",
            """{ "personal": { "path": "C:\\Users\\Me\\Dropbox\\" } }"""));
        env.DropboxConfigurationFiles.Add(new DropboxConfigurationFile(
            @"C:\Users\Me\AppData\Local\Dropbox\info.json",
            """{ "personal": { "path": "C:\\Users\\Me\\Dropbox" } }"""));
        env.ExistingDirectories.Add(@"C:\Users\Me\Dropbox");
        var service = new CloudProviderDiscoveryService(env);

        var target = Assert.Single(service.GetTargets());

        Assert.Equal("dropbox", target.ProviderId);
        Assert.Equal(@"C:\Users\Me\Dropbox", target.RootPath);
    }

    [Fact]
    public void TryMatchPathMatchesNestedPathButNotSiblingPrefix()
    {
        var env = new FakeCloudProviderEnvironment();
        env.AccountCandidates.Add(new OneDriveRootCandidate(@"C:\Users\Me\OneDrive", "Personal"));
        env.ExistingDirectories.Add(@"C:\Users\Me\OneDrive");
        var service = new CloudProviderDiscoveryService(env);

        var metadata = service.TryMatchPath(@"C:\Users\Me\OneDrive\Pictures\photo.jpg");
        var sibling = service.TryMatchPath(@"C:\Users\Me\OneDrive Backup\photo.jpg");

        Assert.NotNull(metadata);
        Assert.Equal("onedrive", metadata.ProviderId);
        Assert.Equal(@"C:\Users\Me\OneDrive", metadata.RootPath);
        Assert.Null(sibling);
    }

    [Fact]
    public void TryMatchPathMatchesNestedPathForNextcloudButNotSiblingPrefix()
    {
        var env = new FakeCloudProviderEnvironment();
        env.ConfigurationFiles.Add(new NextcloudConfigurationFile(
            @"C:\Users\Me\AppData\Roaming\Nextcloud\nextcloud.cfg",
            """
            [Accounts]
            0\url=https://cloud.example.test
            0\FoldersWithPlaceholders\1\localPath=C:/Users/Me/Nextcloud/
            """));
        env.ExistingDirectories.Add(@"C:\Users\Me\Nextcloud");
        var service = new CloudProviderDiscoveryService(env);

        var metadata = service.TryMatchPath(@"C:\Users\Me\Nextcloud\Photos\photo.jpg");
        var sibling = service.TryMatchPath(@"C:\Users\Me\Nextcloud Backup\photo.jpg");

        Assert.NotNull(metadata);
        Assert.Equal("nextcloud", metadata.ProviderId);
        Assert.Equal("Nextcloud", metadata.ProviderName);
        Assert.Equal("cloud.example.test", metadata.AccountLabel);
        Assert.Equal(@"C:\Users\Me\Nextcloud", metadata.RootPath);
        Assert.Null(sibling);
    }

    [Fact]
    public void TryMatchPathMatchesNestedPathForDropboxButNotSiblingPrefix()
    {
        var env = new FakeCloudProviderEnvironment();
        env.DropboxConfigurationFiles.Add(new DropboxConfigurationFile(
            @"C:\Users\Me\AppData\Roaming\Dropbox\info.json",
            """{ "personal": { "path": "C:\\Users\\Me\\Dropbox" } }"""));
        env.ExistingDirectories.Add(@"C:\Users\Me\Dropbox");
        var service = new CloudProviderDiscoveryService(env);

        var metadata = service.TryMatchPath(@"C:\Users\Me\Dropbox\Photos\photo.jpg");
        var sibling = service.TryMatchPath(@"C:\Users\Me\Dropbox Archive\photo.jpg");

        Assert.NotNull(metadata);
        Assert.Equal("dropbox", metadata.ProviderId);
        Assert.Equal("Dropbox", metadata.ProviderName);
        Assert.Equal("Personal", metadata.AccountLabel);
        Assert.Equal(@"C:\Users\Me\Dropbox", metadata.RootPath);
        Assert.Null(sibling);
    }

    [Fact]
    public void TryMatchPathUsesCachedTargetsWithoutRediscovering()
    {
        var env = new FakeCloudProviderEnvironment();
        env.AccountCandidates.Add(new OneDriveRootCandidate(@"C:\Users\Me\OneDrive", "Personal"));
        env.ExistingDirectories.Add(@"C:\Users\Me\OneDrive");
        var service = new CloudProviderDiscoveryService(env);

        Assert.Single(service.GetTargets());
        env.KnownFolderFailure = new UnauthorizedAccessException("known folders blocked");

        var metadata = service.TryMatchPath(@"C:\Users\Me\OneDrive\Pictures\photo.jpg");

        Assert.NotNull(metadata);
        Assert.Equal(@"C:\Users\Me\OneDrive", metadata.RootPath);
    }

    [Fact]
    public void GetTargetsForceRefreshUpdatesCachedTargets()
    {
        var env = new FakeCloudProviderEnvironment();
        env.AccountCandidates.Add(new OneDriveRootCandidate(@"C:\Users\Me\OneDrive", "Personal"));
        env.ExistingDirectories.Add(@"C:\Users\Me\OneDrive");
        var service = new CloudProviderDiscoveryService(env);

        Assert.Single(service.GetTargets());
        env.AccountCandidates.Clear();
        env.ExistingDirectories.Clear();

        Assert.Empty(service.GetTargets(forceRefresh: true));
    }

    [Fact]
    public void TryNormalizeRootPreservesDriveRootSeparator()
    {
        var normalized = CloudProviderDiscoveryService.TryNormalizeRoot(@"C:\", out var root);

        Assert.True(normalized);
        Assert.Equal(@"C:\", root);
    }

    [Theory]
    [InlineData(@"\\")]
    [InlineData(@"\\server")]
    [InlineData(@"\\server\")]
    [InlineData("//")]
    [InlineData("//server")]
    [InlineData("//server/")]
    public void TryNormalizeRootRejectsIncompleteUncPaths(string path)
    {
        var normalized = CloudProviderDiscoveryService.TryNormalizeRoot(path, out var root);

        Assert.False(normalized);
        Assert.Equal(string.Empty, root);
    }

    [Theory]
    [InlineData(@"C:\Users\Me\OneDrive", @"C:\Users\Me\OneDrive", true)]
    [InlineData(@"C:\Users\Me\OneDrive\Pictures", @"C:\Users\Me\OneDrive", true)]
    [InlineData(@"C:\Users\Me\OneDrive Backup", @"C:\Users\Me\OneDrive", false)]
    public void IsNormalizedPathWithinRootMatchesOnlyContainedPaths(string normalizedPath, string normalizedRoot, bool expected)
    {
        Assert.Equal(expected, CloudProviderDiscoveryService.IsNormalizedPathWithinRoot(normalizedPath, normalizedRoot));
    }

    [Fact]
    public void GetTargetsReturnsEmptyWhenEnvironmentIsUnsupported()
    {
        var env = new FakeCloudProviderEnvironment
        {
            IsSupported = false
        };
        env.AccountCandidates.Add(new OneDriveRootCandidate(@"C:\Users\Me\OneDrive", "Personal"));
        env.ExistingDirectories.Add(@"C:\Users\Me\OneDrive");
        var service = new CloudProviderDiscoveryService(env);

        Assert.Empty(service.GetTargets());
        Assert.Null(service.TryMatchPath(@"C:\Users\Me\OneDrive\Pictures\photo.jpg"));
    }

    [Fact]
    public void GetTargetsReturnsEmptyWhenDiscoveryMetadataThrows()
    {
        var env = new FakeCloudProviderEnvironment
        {
            KnownFolderFailure = new UnauthorizedAccessException("known folders blocked")
        };
        env.AccountCandidates.Add(new OneDriveRootCandidate(@"C:\Users\Me\OneDrive", "Personal"));
        env.ExistingDirectories.Add(@"C:\Users\Me\OneDrive");
        var service = new CloudProviderDiscoveryService(env);

        Assert.Empty(service.GetTargets());
        Assert.Null(service.TryMatchPath(@"C:\Users\Me\OneDrive\Pictures\photo.jpg"));
    }

    private sealed class FakeCloudProviderEnvironment : ICloudProviderDiscoveryEnvironment
    {
        public List<OneDriveRootCandidate> AccountCandidates { get; } = [];
        public List<OneDriveRootCandidate> EnvironmentCandidates { get; } = [];
        public List<NextcloudConfigurationFile> ConfigurationFiles { get; } = [];
        public List<string> NextcloudProfileCandidates { get; } = [];
        public List<DropboxConfigurationFile> DropboxConfigurationFiles { get; } = [];
        public List<string> DropboxProfileCandidates { get; } = [];
        public Dictionary<string, string> KnownFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExistingDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsSupported { get; init; } = true;
        public Exception? KnownFolderFailure { get; set; }
        public string? UserProfilePath { get; init; }

        public IEnumerable<OneDriveRootCandidate> GetOneDriveAccountCandidates() => AccountCandidates;
        public IEnumerable<OneDriveRootCandidate> GetOneDriveEnvironmentCandidates() => EnvironmentCandidates;
        public IEnumerable<NextcloudConfigurationFile> GetNextcloudConfigurationFiles() => ConfigurationFiles;
        public IEnumerable<string> GetNextcloudProfileCandidates() => NextcloudProfileCandidates;
        public IEnumerable<DropboxConfigurationFile> GetDropboxConfigurationFiles() => DropboxConfigurationFiles;
        public IEnumerable<string> GetDropboxProfileCandidates() => DropboxProfileCandidates;
        public IReadOnlyDictionary<string, string> GetKnownFolderPaths()
        {
            if (KnownFolderFailure is not null)
            {
                throw KnownFolderFailure;
            }

            return KnownFolders;
        }

        public string? GetUserProfilePath() => UserProfilePath;

        public bool DirectoryExists(string path)
            => ExistingDirectories.Contains(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }
}
