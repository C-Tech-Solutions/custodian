using System.IO.Compression;
using Custodian.Platform.Windows.Services;
using Velopack;

namespace Custodian.Tests;

public sealed class UpdateSecurityTests
{
    [Fact]
    public void UpdateSourceOverrideAcceptsExistingFullyQualifiedLocalFolder()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("custodian-update-source-").FullName;
        try
        {
            var normalized = UpdateSourceOverridePolicy.NormalizeLocalSource(tempDirectory);

            Assert.Equal(Path.GetFullPath(tempDirectory), normalized);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void UpdateSourceOverrideAcceptsLocalFileUri()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("custodian-update-source-uri-").FullName;
        try
        {
            var normalized = UpdateSourceOverridePolicy.NormalizeLocalSource(new Uri(tempDirectory + Path.DirectorySeparatorChar).AbsoluteUri);

            Assert.Equal(Path.GetFullPath(tempDirectory), normalized);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://example.invalid/releases")]
    [InlineData(@"\\server\share\releases")]
    [InlineData(@"relative\releases")]
    public void UpdateSourceOverrideRejectsRemoteOrRelativeSources(string source)
    {
        Assert.ThrowsAny<Exception>(() => UpdateSourceOverridePolicy.NormalizeLocalSource(source));
    }

    [Fact]
    public void PackageVerificationAcceptsAuthorizedCustodianMicrosoftAndJetBrainsFiles()
    {
        var packagePath = CreatePackage(
            "lib/app/Custodian.App.exe",
            "lib/app/Accessibility.dll",
            "lib/app/cli/createdump.exe",
            "lib/app/DirectWriteForwarder.dll",
            "lib/app/cli/mscordaccore_amd64_amd64_10.0.826.23019.dll",
            "lib/app/tui/JetBrains.Annotations.dll");
        try
        {
            var result = UpdatePackageSignatureVerifier.VerifyPackage(
                packagePath,
                new MappingAuthenticodeSignatureVerifier(new Dictionary<string, AuthenticodeSignatureResult>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Custodian.App.exe"] = new(
                        true,
                        "CN=Code Signing, O=C-Tech Solutions LLC, C=US",
                        "Code Signing"),
                    ["Accessibility.dll"] = new(
                        true,
                        "CN=.NET, O=Microsoft Corporation, C=US",
                        ".NET",
                        SignerOrganization: "Microsoft Corporation",
                        CompanyName: "Microsoft Corporation",
                        OriginalFileName: "Accessibility.dll"),
                    ["createdump.exe"] = new(
                        true,
                        "CN=.NET, O=Microsoft Corporation, C=US",
                        ".NET",
                        SignerOrganization: "Microsoft Corporation",
                        CompanyName: "Microsoft Corporation",
                        OriginalFileName: "FX_VER_INTERNALNAME_STR"),
                    ["DirectWriteForwarder.dll"] = new(
                        true,
                        "CN=.NET, O=Microsoft Corporation, C=US",
                        ".NET",
                        SignerOrganization: "Microsoft Corporation",
                        CompanyName: string.Empty,
                        OriginalFileName: "DirectWriteForwarder"),
                    ["mscordaccore_amd64_amd64_10.0.826.23019.dll"] = new(
                        true,
                        "CN=.NET DAC, O=Microsoft Corporation, C=US",
                        ".NET DAC",
                        SignerOrganization: "Microsoft Corporation",
                        CompanyName: "Microsoft Corporation",
                        OriginalFileName: "mscordaccore.dll"),
                    ["JetBrains.Annotations.dll"] = new(
                        true,
                        "CN=JetBrains s.r.o., O=JetBrains s.r.o., C=CZ",
                        "JetBrains s.r.o.",
                        SignerOrganization: "JetBrains s.r.o.",
                        OriginalFileName: "JetBrains.Annotations.dll")
                }));

            Assert.Equal(
                [
                    "lib/app/Custodian.App.exe",
                    "lib/app/Accessibility.dll",
                    "lib/app/cli/createdump.exe",
                    "lib/app/DirectWriteForwarder.dll",
                    "lib/app/cli/mscordaccore_amd64_amd64_10.0.826.23019.dll",
                    "lib/app/tui/JetBrains.Annotations.dll"
                ],
                result.VerifiedFiles);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public void PackageVerificationRejectsUnexpectedSigner()
    {
        var packagePath = CreatePackage("lib/app/Custodian.App.exe");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdatePackageSignatureVerifier.VerifyPackage(
                    packagePath,
                    new FixedAuthenticodeSignatureVerifier(new AuthenticodeSignatureResult(
                        true,
                        "CN=Other Publisher, O=Other Publisher",
                        "Other Publisher"))));

            Assert.Contains("not authorized", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public void PackageVerificationRejectsUnsignedCustodianOwnedPe()
    {
        var packagePath = CreatePackage("lib/app/Custodian.Core.dll");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdatePackageSignatureVerifier.VerifyPackage(
                    packagePath,
                    new FixedAuthenticodeSignatureVerifier(new AuthenticodeSignatureResult(
                        false,
                        FailureReason: "unsigned"))));

            Assert.Contains("Custodian.Core.dll", ex.Message);
            Assert.Contains("unsigned", ex.Message);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public void PackageVerificationRequiresAtLeastOneCustodianOwnedExecutablePayload()
    {
        var packagePath = CreatePackage(
            "lib/app/Accessibility.dll",
            "lib/app/System.Text.Json.dll",
            "lib/app/readme.txt");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdatePackageSignatureVerifier.VerifyPackage(
                    packagePath,
                    new OriginalFileNameAuthenticodeSignatureVerifier()));

            Assert.Contains("did not contain any Custodian-owned executable files", ex.Message);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public void PackageVerificationRejectsRenamedMicrosoftBinaryInPlaceOfCustodianDependency()
    {
        var packagePath = CreatePackage(
            "lib/app/Custodian.App.exe",
            "lib/app/e_sqlite3.dll");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdatePackageSignatureVerifier.VerifyPackage(
                    packagePath,
                    new MappingAuthenticodeSignatureVerifier(new Dictionary<string, AuthenticodeSignatureResult>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Custodian.App.exe"] = CustodianSignature(),
                        ["e_sqlite3.dll"] = new(
                            true,
                            "CN=Microsoft Windows, O=Microsoft Corporation, C=US",
                            "Microsoft Windows",
                            SignerOrganization: "Microsoft Corporation",
                            CompanyName: "Microsoft Corporation",
                            OriginalFileName: "version.dll")
                    })));

            Assert.Contains("e_sqlite3.dll", ex.Message);
            Assert.Contains("not authorized", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public void PackageVerificationRejectsMicrosoftBinaryOutsideApprovedApplicationRoot()
    {
        var packagePath = CreatePackage(
            "lib/app/Custodian.App.exe",
            "tools/Accessibility.dll");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdatePackageSignatureVerifier.VerifyPackage(
                    packagePath,
                    new MappingAuthenticodeSignatureVerifier(new Dictionary<string, AuthenticodeSignatureResult>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Custodian.App.exe"] = CustodianSignature(),
                        ["Accessibility.dll"] = MicrosoftSignature("Accessibility.dll")
                    })));

            Assert.Contains("tools/Accessibility.dll", ex.Message);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public void PackageVerificationRejectsJetBrainsBinaryOutsideExactApprovedPath()
    {
        var packagePath = CreatePackage(
            "lib/app/Custodian.App.exe",
            "lib/app/JetBrains.Annotations.dll");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdatePackageSignatureVerifier.VerifyPackage(
                    packagePath,
                    new MappingAuthenticodeSignatureVerifier(new Dictionary<string, AuthenticodeSignatureResult>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Custodian.App.exe"] = CustodianSignature(),
                        ["JetBrains.Annotations.dll"] = new(
                            true,
                            "CN=JetBrains s.r.o., O=JetBrains s.r.o., C=CZ",
                            "JetBrains s.r.o.",
                            SignerOrganization: "JetBrains s.r.o.",
                            OriginalFileName: "JetBrains.Annotations.dll")
                    })));

            Assert.Contains("not authorized", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public void PackageVerificationRejectsMalformedSignerIdentity()
    {
        var packagePath = CreatePackage("lib/app/Custodian.App.exe");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                UpdatePackageSignatureVerifier.VerifyPackage(
                    packagePath,
                    new FixedAuthenticodeSignatureVerifier(new AuthenticodeSignatureResult(
                        true,
                        "not a distinguished name",
                        "Unknown"))));
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public void CustodianPublisherPolicyRequiresExactOrganizationAttribute()
    {
        Assert.True(UpdatePackageSignatureVerifier.IsTrustedSigner(
            "CN=Code Signing, O=C-Tech Solutions LLC, C=US"));
        Assert.False(UpdatePackageSignatureVerifier.IsTrustedSigner(
            "CN=C-Tech Solutions LLC, O=Other Publisher, C=US"));
        Assert.False(UpdatePackageSignatureVerifier.IsTrustedSigner("not a distinguished name"));
    }

    [Fact]
    public void ArchivePreflightAcceptsExactSizeAndCountBoundaries()
    {
        var entries = Enumerable.Range(0, UpdatePackageSignatureVerifier.MaximumPeEntries)
            .Select(index => new UpdatePackageArchiveEntryMetadata(
                $"lib/app/file-{index}.dll",
                index == 0 ? UpdatePackageSignatureVerifier.MaximumSingleEntryBytes : 0,
                0))
            .Concat(Enumerable.Range(0, UpdatePackageSignatureVerifier.MaximumArchiveEntries - UpdatePackageSignatureVerifier.MaximumPeEntries)
                .Select(index => new UpdatePackageArchiveEntryMetadata($"content/file-{index}.txt", 0, 0)));

        var result = UpdatePackageSignatureVerifier.ValidateArchiveMetadata(entries);

        Assert.Equal(UpdatePackageSignatureVerifier.MaximumArchiveEntries, result.Entries.Count);
        Assert.Equal(UpdatePackageSignatureVerifier.MaximumPeEntries, result.PeCount);
        Assert.Equal(UpdatePackageSignatureVerifier.MaximumSingleEntryBytes, result.TotalUncompressedBytes);
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("lib/./app.dll")]
    [InlineData("C:/app.dll")]
    [InlineData("/lib/app.dll")]
    public void ArchivePreflightRejectsUnsafePaths(string path)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            UpdatePackageSignatureVerifier.ValidateArchiveMetadata(
                [new UpdatePackageArchiveEntryMetadata(path, 1, 1)]));

        Assert.Contains("unsafe archive path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArchivePreflightRejectsCaseInsensitiveNormalizedDuplicates()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            UpdatePackageSignatureVerifier.ValidateArchiveMetadata(
                [
                    new UpdatePackageArchiveEntryMetadata("lib/app/Example.dll", 1, 1),
                    new UpdatePackageArchiveEntryMetadata("lib\\app\\example.dll", 1, 1)
                ]));

        Assert.Contains("duplicate normalized path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArchivePreflightRejectsSingleAndAggregateSizeOveragesWithoutAllocatingPayloads()
    {
        Assert.Throws<InvalidOperationException>(() =>
            UpdatePackageSignatureVerifier.ValidateArchiveMetadata(
                [new UpdatePackageArchiveEntryMetadata("lib/app/large.dll", UpdatePackageSignatureVerifier.MaximumSingleEntryBytes + 1, 1)]));

        var entries = Enumerable.Range(0, 13)
            .Select(index => new UpdatePackageArchiveEntryMetadata(
                $"content/file-{index}.bin",
                UpdatePackageSignatureVerifier.MaximumSingleEntryBytes,
                1));
        var ex = Assert.Throws<InvalidOperationException>(() =>
            UpdatePackageSignatureVerifier.ValidateArchiveMetadata(entries));
        Assert.Contains("total uncompressed-size", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArchivePreflightRejectsInvalidSizeMetadata()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            UpdatePackageSignatureVerifier.ValidateArchiveMetadata(
                [new UpdatePackageArchiveEntryMetadata("lib/app/file.dll", -1, long.MaxValue)]));

        Assert.Contains("invalid size metadata", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WindowsTrustPolicyChecksWholeChainExcludingRootAndDoesNotUseSaferFlag()
    {
        var policy = WindowsAuthenticodeSignatureVerifier.VerificationPolicy;

        Assert.Equal(1u, policy.RevocationChecks);
        Assert.Equal(0x80u, policy.ProviderFlags);
        Assert.Equal(0u, policy.ProviderFlags & 0x100u);
        Assert.Equal(policy, WindowsAuthenticodeSignatureVerifier.NativeDataPolicyForTesting());
    }

    [Fact]
    public void PackageVerifierRunsVelopackChecksumBeforeAuthenticodeInspection()
    {
        var packagePath = CreatePackage("lib/net10.0-windows/Custodian.App.exe");
        var calls = new List<string>();
        try
        {
            var verifier = new VelopackUpdatePackageVerifier(
                new FixedPackagePathResolver(packagePath),
                new RecordingChecksumVerifier(calls),
                new RecordingAuthenticodeSignatureVerifier(
                    calls,
                    new AuthenticodeSignatureResult(true, "CN=C-Tech Solutions LLC, O=C-Tech Solutions LLC", "C-Tech Solutions LLC")));

            verifier.Verify(CreateAsset());

            Assert.Equal(["checksum", "authenticode"], calls);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public void PackageVerifierRejectsNullAssetBeforeResolvingPackagePath()
    {
        var verifier = new VelopackUpdatePackageVerifier(
            new FixedPackagePathResolver("unused.nupkg"),
            new RecordingChecksumVerifier([]),
            new FixedAuthenticodeSignatureVerifier(
                new AuthenticodeSignatureResult(true, "CN=C-Tech Solutions LLC, O=C-Tech Solutions LLC", "C-Tech Solutions LLC")));

        Assert.Throws<ArgumentNullException>(() => verifier.Verify(null!));
    }

    [Fact]
    public void ChecksumVerifierRejectsNullAsset()
    {
        var verifier = new VelopackUpdatePackageChecksumVerifier();

        Assert.Throws<ArgumentNullException>(() => verifier.Verify(null!, "package.nupkg"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ChecksumVerifierRejectsMissingPackagePath(string? packagePath)
    {
        var verifier = new VelopackUpdatePackageChecksumVerifier();

        Assert.ThrowsAny<ArgumentException>(() => verifier.Verify(CreateAsset(), packagePath!));
    }

    [Fact]
    public void PackageSignatureVerifierRejectsNullSignatureVerifier()
    {
        Assert.Throws<ArgumentNullException>(() =>
            UpdatePackageSignatureVerifier.VerifyPackage("package.nupkg", null!));
    }

    [Fact]
    public void PackageVerificationRejectsUnsignedThirdPartyPe()
    {
        var packagePath = CreatePackage(
            "lib/net10.0-windows/Custodian.App.exe",
            "lib/net10.0-windows/Accessibility.dll");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdatePackageSignatureVerifier.VerifyPackage(
                    packagePath,
                    new MappingAuthenticodeSignatureVerifier(new Dictionary<string, AuthenticodeSignatureResult>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Custodian.App.exe"] = new(
                            true,
                            "CN=C-Tech Solutions LLC, O=C-Tech Solutions LLC",
                            "C-Tech Solutions LLC"),
                        ["Accessibility.dll"] = new(false, FailureReason: "unsigned")
                    })));

            Assert.Contains("Accessibility.dll", ex.Message);
            Assert.Contains("unsigned", ex.Message);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public void WindowsAuthenticodeVerifierAcceptsSignedDotnetHost()
    {
        var runtimeDirectory = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot = runtimeDirectory.Parent?.Parent?.Parent;
        Assert.NotNull(dotnetRoot);
        var dotnetHost = Path.Combine(dotnetRoot.FullName, "dotnet.exe");
        Assert.True(File.Exists(dotnetHost), $"The .NET host was not found at '{dotnetHost}'.");

        var result = new WindowsAuthenticodeSignatureVerifier().Verify(dotnetHost);

        Assert.True(result.IsTrusted, result.FailureReason);
        Assert.Contains("Microsoft Corporation", result.SignerSubject, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Microsoft Corporation", result.SignerOrganization, ignoreCase: true);
        Assert.Equal("Microsoft Corporation", result.CompanyName, ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(result.OriginalFileName));
    }

    [SignedReleasePackageFact]
    public void ConfiguredSignedReleasePackagePassesFullPublisherPolicy()
    {
        var packagePath = Environment.GetEnvironmentVariable("CUSTODIAN_TEST_SIGNED_RELEASE_PACKAGE")!;

        Assert.True(File.Exists(packagePath), $"Configured signed release package was not found: {packagePath}");

        var result = UpdatePackageSignatureVerifier.VerifyPackage(
            packagePath,
            new WindowsAuthenticodeSignatureVerifier());

        using var archive = ZipFile.OpenRead(packagePath);
        var expectedPeCount = archive.Entries.Count(entry =>
        {
            var extension = Path.GetExtension(Path.GetFileName(entry.FullName));
            return string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(expectedPeCount, result.VerifiedFiles.Count);
    }

    private static AuthenticodeSignatureResult CustodianSignature()
        => new(
            true,
            "CN=Code Signing, O=C-Tech Solutions LLC, C=US",
            "Code Signing",
            SignerOrganization: "C-Tech Solutions LLC");

    private static AuthenticodeSignatureResult MicrosoftSignature(string originalFileName)
        => new(
            true,
            "CN=.NET, O=Microsoft Corporation, C=US",
            ".NET",
            SignerOrganization: "Microsoft Corporation",
            CompanyName: "Microsoft Corporation",
            OriginalFileName: originalFileName);

    private static VelopackAsset CreateAsset() => new()
    {
        FileName = "Custodian.nupkg",
        SHA256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    };

    private static string CreatePackage(params string[] entries)
    {
        var packagePath = Path.Combine(Path.GetTempPath(), $"custodian-update-{Guid.NewGuid():N}.nupkg");
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        foreach (var entryName in entries)
        {
            var entry = archive.CreateEntry(entryName);
            using var stream = entry.Open();
            stream.Write([0x4D, 0x5A]);
        }

        return packagePath;
    }

    private sealed class FixedPackagePathResolver(string packagePath) : IUpdatePackagePathResolver
    {
        public string GetPackagePath(VelopackAsset asset) => packagePath;
    }

    private sealed class RecordingChecksumVerifier(List<string> calls) : IUpdatePackageChecksumVerifier
    {
        public void Verify(VelopackAsset asset, string packagePath) => calls.Add("checksum");
    }

    private sealed class FixedAuthenticodeSignatureVerifier(AuthenticodeSignatureResult result) : IAuthenticodeSignatureVerifier
    {
        public AuthenticodeSignatureResult Verify(string filePath) => result;
    }

    private sealed class MappingAuthenticodeSignatureVerifier(
        IReadOnlyDictionary<string, AuthenticodeSignatureResult> results) : IAuthenticodeSignatureVerifier
    {
        public AuthenticodeSignatureResult Verify(string filePath)
            => results[Path.GetFileName(filePath)];
    }

    private sealed class OriginalFileNameAuthenticodeSignatureVerifier : IAuthenticodeSignatureVerifier
    {
        public AuthenticodeSignatureResult Verify(string filePath)
            => MicrosoftSignature(Path.GetFileName(filePath));
    }

    private sealed class SignedReleasePackageFactAttribute : FactAttribute
    {
        public SignedReleasePackageFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CUSTODIAN_TEST_SIGNED_RELEASE_PACKAGE")))
            {
                Skip = "Set CUSTODIAN_TEST_SIGNED_RELEASE_PACKAGE to run the signed package integration test.";
            }
        }
    }

    private sealed class RecordingAuthenticodeSignatureVerifier(
        List<string> calls,
        AuthenticodeSignatureResult result) : IAuthenticodeSignatureVerifier
    {
        public AuthenticodeSignatureResult Verify(string filePath)
        {
            calls.Add("authenticode");
            return result;
        }
    }
}
