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
    public void TryNormalizeRootPreservesDriveRootSeparator()
    {
        var normalized = CloudProviderDiscoveryService.TryNormalizeRoot(@"C:\", out var root);

        Assert.True(normalized);
        Assert.Equal(@"C:\", root);
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
        public Dictionary<string, string> KnownFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExistingDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsSupported { get; init; } = true;
        public Exception? KnownFolderFailure { get; init; }
        public string? UserProfilePath { get; init; }

        public IEnumerable<OneDriveRootCandidate> GetOneDriveAccountCandidates() => AccountCandidates;
        public IEnumerable<OneDriveRootCandidate> GetOneDriveEnvironmentCandidates() => EnvironmentCandidates;
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
