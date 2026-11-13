using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Custodian.Platform.Windows.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

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
    public void IndependentCertificateChainPolicyChecksOnlineRevocationExcludingRoot()
    {
        var policy = WindowsCertificateChainVerifier.VerificationPolicy;

        Assert.Equal(X509RevocationMode.Online, policy.RevocationMode);
        Assert.Equal(X509RevocationFlag.ExcludeRoot, policy.RevocationFlag);
        Assert.Equal(X509VerificationFlags.IgnoreNotTimeValid, policy.VerificationFlags);
        Assert.Equal(policy, WindowsCertificateChainVerifier.NativePolicyForTesting());
    }

    [Fact]
    public void WindowsAuthenticodeVerifierRejectsUnavailableIndependentRevocationStatusAfterWinTrustSuccess()
    {
        using var signer = CreateTestSignerCertificate();
        var signerBytes = signer.Export(X509ContentType.Cert);
        var verifier = new WindowsAuthenticodeSignatureVerifier(
            _ => 0,
            _ => X509CertificateLoader.LoadCertificate(signerBytes),
            new FixedCertificateChainVerifier(new CertificateChainVerificationResult(
                false,
                "certificate chain validation failed: RevocationStatusUnknown, OfflineRevocation")));

        var result = verifier.Verify(Environment.ProcessPath!);

        Assert.False(result.IsTrusted);
        Assert.Contains("RevocationStatusUnknown", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains("OfflineRevocation", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsAuthenticodeVerifierAcceptsIndependentChainSuccessAfterWinTrustSuccess()
    {
        using var signer = CreateTestSignerCertificate();
        var signerBytes = signer.Export(X509ContentType.Cert);
        var verifier = new WindowsAuthenticodeSignatureVerifier(
            _ => 0,
            _ => X509CertificateLoader.LoadCertificate(signerBytes),
            new FixedCertificateChainVerifier(new CertificateChainVerificationResult(true)));

        var result = verifier.Verify(Environment.ProcessPath!);

        Assert.True(result.IsTrusted, result.FailureReason);
        Assert.Contains("C-Tech Solutions LLC", result.SignerSubject, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageVerificationChecksIndependentChainOncePerSignerPerPackage()
    {
        var packagePath = CreatePackage(
            "lib/app/Custodian.App.exe",
            "lib/app/Custodian.Core.dll");
        using var signer = CreateTestSignerCertificate();
        var signerBytes = signer.Export(X509ContentType.Cert);
        var chainVerifier = new CountingCertificateChainVerifier();
        var verifier = new WindowsAuthenticodeSignatureVerifier(
            _ => 0,
            _ => X509CertificateLoader.LoadCertificate(signerBytes),
            chainVerifier);
        try
        {
            var result = UpdatePackageSignatureVerifier.VerifyPackage(packagePath, verifier);
            var secondResult = UpdatePackageSignatureVerifier.VerifyPackage(packagePath, verifier);

            Assert.Equal(2, result.VerifiedFiles.Count);
            Assert.Equal(2, secondResult.VerifiedFiles.Count);
            Assert.Equal(2, chainVerifier.CallCount);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public void PackageVerifierRunsVelopackChecksumBeforeAuthenticodeInspection()
    {
        var packagePath = CreatePackage("lib/app/Custodian.App.exe");
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
            if (entry.FullName.EndsWith('/'))
            {
                return false;
            }

            var extension = Path.GetExtension(Path.GetFileName(entry.FullName));
            if (string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            using var stream = entry.Open();
            var header = new byte[2];
            return stream.ReadAtLeast(header, 2, throwOnEndOfStream: false) == 2 && header[0] == 'M' && header[1] == 'Z';
        });
        Assert.Equal(expectedPeCount, result.VerifiedFiles.Count);
    }

    [Fact]
    public void PackageVerificationChecksEveryFileWithAnMzHeaderWhateverItsName()
    {
        var packagePath = CreatePackageWithEntries(
            Manifest("Custodian.App.exe"),
            Pe("lib/app/Custodian.App.exe"),
            Pe("lib/app/helper.com"),
            Pe("lib/app/plugins/loader"),
            Text("lib/app/readme.txt"));
        try
        {
            var result = UpdatePackageSignatureVerifier.VerifyPackage(
                packagePath,
                new MappingAuthenticodeSignatureVerifier(new Dictionary<string, AuthenticodeSignatureResult>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Custodian.App.exe"] = CustodianSignature(),
                    ["helper.com"] = CustodianSignature(),
                    ["loader"] = CustodianSignature()
                }));

            Assert.Equal(["lib/app/Custodian.App.exe", "lib/app/helper.com", "lib/app/plugins/loader"], result.VerifiedFiles);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public void PackageVerificationRejectsUnsignedPeWithANonExecutableName()
    {
        var packagePath = CreatePackageWithEntries(
            Manifest("Custodian.App.exe"),
            Pe("lib/app/Custodian.App.exe"),
            Pe("lib/app/data.bin"));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdatePackageSignatureVerifier.VerifyPackage(
                    packagePath,
                    new MappingAuthenticodeSignatureVerifier(new Dictionary<string, AuthenticodeSignatureResult>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Custodian.App.exe"] = CustodianSignature(),
                        ["data.bin"] = new(false, FailureReason: "unsigned")
                    })));

            Assert.Contains("data.bin", ex.Message);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public void PackageVerificationStillChecksExeNamedFilesWithoutAnMzHeader()
    {
        // Velopack extracts the first entry ending in "Squirrel.exe" as Update.exe, whatever it contains.
        var packagePath = CreatePackageWithEntries(
            Manifest("Custodian.App.exe"),
            Pe("lib/app/Custodian.App.exe"),
            Text("lib/app/Squirrel.exe"));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdatePackageSignatureVerifier.VerifyPackage(
                    packagePath,
                    new MappingAuthenticodeSignatureVerifier(new Dictionary<string, AuthenticodeSignatureResult>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Custodian.App.exe"] = CustodianSignature(),
                        ["Squirrel.exe"] = new(false, FailureReason: "not a PE file")
                    })));

            Assert.Contains("Squirrel.exe", ex.Message);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Theory]
    [InlineData("<package><metadata><id>Custodian</id></metadata></package>", "exactly one mainExe")]
    [InlineData("<package><metadata><mainExe>Custodian.App.exe</mainExe><mainExe>other.exe</mainExe></metadata></package>", "exactly one mainExe")]
    // Velopack's native updater would launch the last mainExe it sees, at any depth.
    [InlineData("<package><metadata><mainExe>Custodian.App.exe</mainExe></metadata><mainExe>C:\\Windows\\notepad.exe</mainExe></package>", "exactly one mainExe")]
    [InlineData("<package><metadata><mainExe>Custodian.App.exe</mainExe></metadata><files><mainExe>other.exe</mainExe></files></package>", "exactly one mainExe")]
    [InlineData("<package><metadata><group><mainExe>Custodian.App.exe</mainExe></group></metadata></package>", "exactly one mainExe")]
    [InlineData("<package><metadata><mainExe>Custodian.<!-- x -->App.exe</mainExe></metadata></package>", "plain text")]
    [InlineData("<package><metadata><mainExe><![CDATA[Custodian.App.exe]]></mainExe></metadata></package>", "plain text")]
    [InlineData("<package><metadata><mainExe>Custodian.App.exe<b>x</b></mainExe></metadata></package>", "plain text")]
    [InlineData("<package><metadata><mainExe> Custodian.App.exe </mainExe></metadata></package>", "plain text")]
    [InlineData("<package><metadata><mainExe>   </mainExe></metadata></package>", "plain text")]
    [InlineData("<package><metadata><mainExe>../Custodian.App.exe</mainExe></metadata></package>", "unsafe mainExe")]
    [InlineData("<package><metadata><mainExe>C:/Windows/notepad.exe</mainExe></metadata></package>", "unsafe mainExe")]
    [InlineData("<package><metadata><mainExe>Custodian.App.exe/</mainExe></metadata></package>", "unsafe mainExe")]
    [InlineData("<package><metadata><mainExe>Custodian.App.exe\\</mainExe></metadata></package>", "unsafe mainExe")]
    [InlineData("<package><metadata><mainExe>Accessibility.dll</mainExe></metadata></package>", "not a Custodian-signed file")]
    [InlineData("<package><metadata><mainExe>readme.txt</mainExe></metadata></package>", "not a Custodian-signed file")]
    [InlineData("<package><metadata><mainExe>missing.exe</mainExe></metadata></package>", "not a Custodian-signed file")]
    [InlineData("<!DOCTYPE package [<!ENTITY a \"aaaa\">]><package><metadata><mainExe>&a;</mainExe></metadata></package>", "not valid XML")]
    [InlineData("<package><metadata><mainExe>Custodian.App.exe</mainExe>", "not valid XML")]
    public void PackageVerificationRequiresMainExeToBeACustodianSignedAppFile(string manifest, string expectedMessage)
    {
        var packagePath = CreatePackageWithEntries(
            ("Custodian.DiskAnalyzer.nuspec", Encoding.UTF8.GetBytes(manifest)),
            Pe("lib/app/Custodian.App.exe"),
            Pe("lib/app/Accessibility.dll"),
            Text("lib/app/readme.txt"));
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

            Assert.Contains(expectedMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public void PackageVerificationAcceptsNamespacedManifestLikeVpkWrites()
    {
        var packagePath = CreatePackageWithEntries(
            ("Custodian.DiskAnalyzer.nuspec", Encoding.UTF8.GetBytes("""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
                <metadata>
                <id>Custodian.DiskAnalyzer</id>
                <version>1.5.7</version>
                <mainExe>Custodian.App.exe</mainExe>
                </metadata>
                </package>
                """)),
            Pe("lib/app/Custodian.App.exe"));
        try
        {
            var result = UpdatePackageSignatureVerifier.VerifyPackage(
                packagePath,
                new FixedAuthenticodeSignatureVerifier(CustodianSignature()));

            Assert.Equal(["lib/app/Custodian.App.exe"], result.VerifiedFiles);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void PackageVerificationRequiresExactlyOneManifest(int manifestCount)
    {
        var entries = Enumerable.Range(0, manifestCount)
            .Select(index => ($"manifest-{index}.nuspec", Encoding.UTF8.GetBytes(ManifestXml("Custodian.App.exe"))))
            .Append(Pe("lib/app/Custodian.App.exe"))
            .ToArray();
        var packagePath = CreatePackageWithEntries(entries);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdatePackageSignatureVerifier.VerifyPackage(
                    packagePath,
                    new FixedAuthenticodeSignatureVerifier(CustodianSignature())));

            Assert.Contains(".nuspec manifest", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public void PackageVerificationRejectsOversizedManifest()
    {
        var manifest = ManifestXml("Custodian.App.exe") +
            new string(' ', (int)UpdatePackageSignatureVerifier.MaximumManifestBytes);
        var packagePath = CreatePackageWithEntries(
            ("Custodian.DiskAnalyzer.nuspec", Encoding.UTF8.GetBytes(manifest)),
            Pe("lib/app/Custodian.App.exe"));
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdatePackageSignatureVerifier.VerifyPackage(
                    packagePath,
                    new FixedAuthenticodeSignatureVerifier(CustodianSignature())));

            Assert.Contains("limit", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Theory]
    [InlineData(@"\\attacker\share\Custodian.nupkg")]
    [InlineData(@"C:\Users\Public\Custodian.nupkg")]
    [InlineData("C:Custodian.nupkg")]
    [InlineData(@"..\Custodian.nupkg")]
    [InlineData("releases/Custodian.nupkg")]
    [InlineData("Custodian.exe")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData(null)]
    public void PackageFileNamePolicyRejectsAnythingButAPlainPackageName(string? fileName)
    {
        Assert.Throws<InvalidOperationException>(() => UpdatePackageFileNamePolicy.EnsureSafe(fileName));
    }

    [Fact]
    public void PackageFileNamePolicyAcceptsReleasePackageName()
    {
        UpdatePackageFileNamePolicy.EnsureSafe("Custodian.DiskAnalyzer-1.5.7-perf.2-full.nupkg");
    }

    [Fact]
    public void PackagePathResolverRejectsUncFileNameFromFeed()
    {
        using var directory = new TempDirectory();
        var resolver = new VelopackUpdatePackagePathResolver(
            new TestVelopackLocator("custodian-test", "1.0.0", directory.Path));

        Assert.Throws<InvalidOperationException>(() =>
            resolver.GetPackagePath(new VelopackAsset { FileName = @"\\attacker\share\Custodian.nupkg" }));
    }

    [Fact]
    public async Task StagerMovesPackageIntoPlaceOnlyAfterItVerifies()
    {
        using var directory = new TempDirectory();
        var asset = new VelopackAsset { FileName = "Custodian-2.0.0-full.nupkg" };
        var finalPath = Path.Combine(directory.Path, asset.FileName);
        string? verifiedPath = null;

        var stagedPath = await VerifiedUpdatePackageStager.StageAsync(
            asset,
            directory.Path,
            (path, _) => File.WriteAllTextAsync(path, "package"),
            path =>
            {
                verifiedPath = path;
                Assert.False(File.Exists(finalPath), "The package reached its final path before it was verified.");
            },
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(finalPath, stagedPath);
        Assert.NotNull(verifiedPath);
        Assert.NotEqual(finalPath, verifiedPath);
        Assert.Equal("package", File.ReadAllText(finalPath));
        Assert.Equal([finalPath], Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task StagerLeavesNothingBehindWhenVerificationFails()
    {
        using var directory = new TempDirectory();
        var asset = new VelopackAsset { FileName = "Custodian-2.0.0-full.nupkg" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => VerifiedUpdatePackageStager.StageAsync(
            asset,
            directory.Path,
            (path, _) => File.WriteAllTextAsync(path, "tampered"),
            _ => throw new InvalidOperationException("unsigned"),
            NullLogger.Instance,
            CancellationToken.None));

        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task StagerReusesAnExistingPackageThatVerifies()
    {
        using var directory = new TempDirectory();
        var asset = new VelopackAsset { FileName = "Custodian-2.0.0-full.nupkg" };
        var finalPath = Path.Combine(directory.Path, asset.FileName);
        File.WriteAllText(finalPath, "package");
        var downloads = 0;

        await VerifiedUpdatePackageStager.StageAsync(
            asset,
            directory.Path,
            (_, _) =>
            {
                downloads++;
                return Task.CompletedTask;
            },
            _ => { },
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal(0, downloads);
        Assert.Equal("package", File.ReadAllText(finalPath));
    }

    [Fact]
    public async Task StagerReplacesAnExistingPackageThatFailsVerification()
    {
        using var directory = new TempDirectory();
        var asset = new VelopackAsset { FileName = "Custodian-2.0.0-full.nupkg" };
        var finalPath = Path.Combine(directory.Path, asset.FileName);
        File.WriteAllText(finalPath, "tampered");

        await VerifiedUpdatePackageStager.StageAsync(
            asset,
            directory.Path,
            (path, _) => File.WriteAllTextAsync(path, "package"),
            path =>
            {
                if (File.ReadAllText(path) != "package")
                {
                    throw new InvalidOperationException("unsigned");
                }
            },
            NullLogger.Instance,
            CancellationToken.None);

        Assert.Equal("package", File.ReadAllText(finalPath));
        Assert.Equal([finalPath], Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task StagerDoesNotPlaceAPackageWhenCancelledDuringVerification()
    {
        using var directory = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        var asset = new VelopackAsset { FileName = "Custodian-2.0.0-full.nupkg" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VerifiedUpdatePackageStager.StageAsync(
            asset,
            directory.Path,
            (path, _) => File.WriteAllTextAsync(path, "package"),
            _ => cancellation.Cancel(),
            NullLogger.Instance,
            cancellation.Token));

        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task StagerReportsCancellationWhenCancelledWhileCheckingAnExistingPackage()
    {
        using var directory = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        var asset = new VelopackAsset { FileName = "Custodian-2.0.0-full.nupkg" };
        var finalPath = Path.Combine(directory.Path, asset.FileName);
        File.WriteAllText(finalPath, "package");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VerifiedUpdatePackageStager.StageAsync(
            asset,
            directory.Path,
            (_, _) => Task.CompletedTask,
            _ => cancellation.Cancel(),
            NullLogger.Instance,
            cancellation.Token));

        // The verified package stays for the next attempt; it is not treated as failing verification.
        Assert.Equal("package", File.ReadAllText(finalPath));
    }

    [Fact]
    public async Task StagerRejectsUnsafeFileNameBeforeDownloading()
    {
        using var directory = new TempDirectory();
        var downloads = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => VerifiedUpdatePackageStager.StageAsync(
            new VelopackAsset { FileName = @"\\attacker\share\Custodian.nupkg" },
            directory.Path,
            (_, _) =>
            {
                downloads++;
                return Task.CompletedTask;
            },
            _ => { },
            NullLogger.Instance,
            CancellationToken.None));

        Assert.Equal(0, downloads);
    }

    [Fact]
    public async Task VerifyingUpdateManagerExtractsUpdateExeOnlyFromAVerifiedPackage()
    {
        using var fixture = new UpdateManagerFixture();
        var source = new CopyingUpdateSource(fixture.SourcePackagePath);
        var manager = fixture.CreateManager(source, new FixedAuthenticodeSignatureVerifier(CustodianSignature()));

        await manager.DownloadUpdatesAsync(new UpdateInfo(fixture.Asset, isDowngrade: false));

        Assert.Equal(1, source.Downloads);
        Assert.Equal(UpdateManagerFixture.NewUpdateExe, File.ReadAllBytes(fixture.UpdateExePath));
        Assert.Equal(
            [Path.Combine(fixture.PackagesDirectory, fixture.Asset.FileName)],
            Directory.GetFiles(fixture.PackagesDirectory, "*.nupkg"));
        Assert.Empty(Directory.GetFiles(fixture.PackagesDirectory, "*.partial"));
    }

    [Fact]
    public async Task VerifyingUpdateManagerLeavesUpdateExeAloneWhenThePackageFailsVerification()
    {
        using var fixture = new UpdateManagerFixture();
        var source = new CopyingUpdateSource(fixture.SourcePackagePath);
        var manager = fixture.CreateManager(
            source,
            new MappingAuthenticodeSignatureVerifier(new Dictionary<string, AuthenticodeSignatureResult>(StringComparer.OrdinalIgnoreCase)
            {
                ["Custodian.App.exe"] = CustodianSignature(),
                ["Squirrel.exe"] = new(false, FailureReason: "unsigned")
            }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.DownloadUpdatesAsync(new UpdateInfo(fixture.Asset, isDowngrade: false)));

        Assert.Contains("Squirrel.exe", ex.Message);
        Assert.Equal(UpdateManagerFixture.OldUpdateExe, File.ReadAllBytes(fixture.UpdateExePath));
        Assert.Empty(Directory.GetFiles(fixture.PackagesDirectory, "*.nupkg"));
        Assert.Empty(Directory.GetFiles(fixture.PackagesDirectory, "*.partial"));
    }

    [Fact]
    public async Task VerifyingUpdateManagerReportsCancellationDuringVerification()
    {
        using var fixture = new UpdateManagerFixture();
        using var cancellation = new CancellationTokenSource();
        var source = new CopyingUpdateSource(fixture.SourcePackagePath);
        var manager = fixture.CreateManager(
            source,
            new CancellingAuthenticodeSignatureVerifier(cancellation, CustodianSignature()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.DownloadUpdatesAsync(new UpdateInfo(fixture.Asset, isDowngrade: false), cancelToken: cancellation.Token));

        Assert.Equal(UpdateManagerFixture.OldUpdateExe, File.ReadAllBytes(fixture.UpdateExePath));
        Assert.Empty(Directory.GetFiles(fixture.PackagesDirectory, "*.nupkg"));
        Assert.Empty(Directory.GetFiles(fixture.PackagesDirectory, "*.partial"));
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

    private static X509Certificate2 CreateTestSignerCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Code Signing, O=C-Tech Solutions LLC, C=US",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    private static VelopackAsset CreateAsset() => new()
    {
        FileName = "Custodian.nupkg",
        SHA256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    };

    /// <summary>Creates a package with a manifest naming Custodian.App.exe and one MZ file per entry.</summary>
    private static string CreatePackage(params string[] entries)
        => CreatePackageWithEntries([Manifest("Custodian.App.exe"), .. entries.Select(Pe)]);

    private static string CreatePackageWithEntries(params (string Name, byte[] Content)[] entries)
    {
        var packagePath = Path.Combine(Path.GetTempPath(), $"custodian-update-{Guid.NewGuid():N}.nupkg");
        WritePackage(packagePath, entries);
        return packagePath;
    }

    private static void WritePackage(string packagePath, params (string Name, byte[] Content)[] entries)
    {
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(content);
        }
    }

    private static string ManifestXml(string mainExe)
        => $"<package><metadata><id>Custodian.DiskAnalyzer</id><version>2.0.0</version><mainExe>{mainExe}</mainExe></metadata></package>";

    private static (string Name, byte[] Content) Manifest(string mainExe)
        => ("Custodian.DiskAnalyzer.nuspec", Encoding.UTF8.GetBytes(ManifestXml(mainExe)));

    private static (string Name, byte[] Content) Pe(string name) => (name, [0x4D, 0x5A]);

    private static (string Name, byte[] Content) Text(string name) => (name, Encoding.UTF8.GetBytes("not an executable"));

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = Directory.CreateTempSubdirectory("custodian-update-test-").FullName;
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temp folder does not affect the test result.
            }
        }
    }

    /// <summary>
    /// An installed-app layout for <see cref="VerifyingUpdateManager"/>: a packages folder, an
    /// existing Update.exe, and a feed package whose Squirrel.exe is the new Update.exe.
    /// </summary>
    private sealed class UpdateManagerFixture : IDisposable
    {
        public static readonly byte[] OldUpdateExe = Encoding.ASCII.GetBytes("MZ old update");
        public static readonly byte[] NewUpdateExe = Encoding.ASCII.GetBytes("MZ new update");

        private readonly TempDirectory _root = new();

        public UpdateManagerFixture()
        {
            PackagesDirectory = System.IO.Path.Combine(_root.Path, "packages");
            AppDirectory = System.IO.Path.Combine(_root.Path, "current");
            var feedDirectory = System.IO.Path.Combine(_root.Path, "feed");
            Directory.CreateDirectory(PackagesDirectory);
            Directory.CreateDirectory(AppDirectory);
            Directory.CreateDirectory(feedDirectory);

            UpdateExePath = System.IO.Path.Combine(_root.Path, "Update.exe");
            File.WriteAllBytes(UpdateExePath, OldUpdateExe);

            SourcePackagePath = System.IO.Path.Combine(feedDirectory, "Custodian.DiskAnalyzer-2.0.0-full.nupkg");
            WritePackage(
                SourcePackagePath,
                Manifest("Custodian.App.exe"),
                Pe("lib/app/Custodian.App.exe"),
                ("lib/app/Squirrel.exe", NewUpdateExe));

            var packageBytes = File.ReadAllBytes(SourcePackagePath);
            Asset = new VelopackAsset
            {
                Type = VelopackAssetType.Full,
                FileName = System.IO.Path.GetFileName(SourcePackagePath),
                Size = packageBytes.Length,
                SHA256 = Convert.ToHexString(SHA256.HashData(packageBytes))
            };
        }

        public string PackagesDirectory { get; }

        public string AppDirectory { get; }

        public string UpdateExePath { get; }

        public string SourcePackagePath { get; }

        public VelopackAsset Asset { get; }

        public UpdateManager CreateManager(IUpdateSource source, IAuthenticodeSignatureVerifier signatureVerifier)
        {
            var locator = new TestVelopackLocator(
                "Custodian.DiskAnalyzer.Tests",
                "1.0.0",
                PackagesDirectory,
                AppDirectory,
                _root.Path,
                UpdateExePath);
            return new VerifyingUpdateManager(
                source,
                options: null,
                NullLogger.Instance,
                locator,
                new VelopackUpdatePackageVerifier(locator, signatureVerifier));
        }

        public void Dispose() => _root.Dispose();
    }

    private sealed class CopyingUpdateSource(string packagePath) : IUpdateSource
    {
        public int Downloads { get; private set; }

        public Task<VelopackAssetFeed> GetReleaseFeed(
            ILogger logger,
            string channel,
            Guid? stagingId = null,
            VelopackAsset? latestLocalRelease = null)
            => throw new NotSupportedException();

        public Task DownloadReleaseEntry(
            ILogger logger,
            VelopackAsset releaseEntry,
            string localFile,
            Action<int> progress,
            CancellationToken cancelToken = default)
        {
            Downloads++;
            File.Copy(packagePath, localFile, overwrite: true);
            return Task.CompletedTask;
        }
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

    private sealed class CancellingAuthenticodeSignatureVerifier(
        CancellationTokenSource cancellation,
        AuthenticodeSignatureResult result) : IAuthenticodeSignatureVerifier
    {
        public AuthenticodeSignatureResult Verify(string filePath)
        {
            cancellation.Cancel();
            return result;
        }
    }

    private sealed class FixedCertificateChainVerifier(CertificateChainVerificationResult result) : ICertificateChainVerifier
    {
        public CertificateChainVerificationResult Verify(X509Certificate2 certificate) => result;
    }

    private sealed class CountingCertificateChainVerifier : ICertificateChainVerifier
    {
        public int CallCount { get; private set; }

        public CertificateChainVerificationResult Verify(X509Certificate2 certificate)
        {
            CallCount++;
            return new CertificateChainVerificationResult(true);
        }
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
