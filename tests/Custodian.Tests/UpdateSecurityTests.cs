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
    public void PackageVerificationAcceptsCustodianOwnedPeSignedByTrustedOrganization()
    {
        var packagePath = CreatePackage("lib/net10.0-windows/Custodian.App.exe");
        try
        {
            var result = UpdatePackageSignatureVerifier.VerifyPackage(
                packagePath,
                new FixedAuthenticodeSignatureVerifier(new AuthenticodeSignatureResult(
                    true,
                    "CN=Code Signing, O=C-Tech Solutions LLC, C=US",
                    "Code Signing")));

            Assert.Equal(["lib/net10.0-windows/Custodian.App.exe"], result.VerifiedFiles);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public void PackageVerificationRejectsUnexpectedSigner()
    {
        var packagePath = CreatePackage("lib/net10.0-windows/Custodian.App.exe");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdatePackageSignatureVerifier.VerifyPackage(
                    packagePath,
                    new FixedAuthenticodeSignatureVerifier(new AuthenticodeSignatureResult(
                        true,
                        "CN=Other Publisher, O=Other Publisher",
                        "Other Publisher"))));

            Assert.Contains("not 'C-Tech Solutions LLC'", ex.Message);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public void PackageVerificationRequiresAtLeastOneCustodianOwnedPe()
    {
        var packagePath = CreatePackage("lib/net10.0-windows/Velopack.dll");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UpdatePackageSignatureVerifier.VerifyPackage(
                    packagePath,
                    new FixedAuthenticodeSignatureVerifier(new AuthenticodeSignatureResult(
                        true,
                        "CN=C-Tech Solutions LLC",
                        "C-Tech Solutions LLC"))));

            Assert.Contains("did not contain any Custodian-owned executable files", ex.Message);
        }
        finally
        {
            File.Delete(packagePath);
        }
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
                    new AuthenticodeSignatureResult(true, "CN=C-Tech Solutions LLC", "C-Tech Solutions LLC")));

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
                new AuthenticodeSignatureResult(true, "CN=C-Tech Solutions LLC", "C-Tech Solutions LLC")));

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
