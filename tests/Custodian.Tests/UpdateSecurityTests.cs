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
    public void PackageVerificationAcceptsTrustedCustodianAndFrameworkPeFiles()
    {
        var packagePath = CreatePackage(
            "lib/net10.0-windows/Custodian.App.exe",
            "lib/net10.0-windows/Accessibility.dll",
            "lib/net10.0-windows/Velopack.dll");
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
                        ".NET"),
                    ["Velopack.dll"] = new(
                        true,
                        "CN=Velopack Publisher, O=Velopack Publisher",
                        "Velopack Publisher")
                }));

            Assert.Equal(
                [
                    "lib/net10.0-windows/Custodian.App.exe",
                    "lib/net10.0-windows/Accessibility.dll",
                    "lib/net10.0-windows/Velopack.dll"
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
    public void PackageVerificationRejectsUnsignedCustodianOwnedPe()
    {
        var packagePath = CreatePackage("lib/net10.0-windows/Custodian.Core.dll");
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
            "lib/net10.0-windows/Accessibility.dll",
            "lib/net10.0-windows/Velopack.dll",
            "lib/net10.0-windows/readme.txt");
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
                            "CN=C-Tech Solutions LLC",
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

    private sealed class MappingAuthenticodeSignatureVerifier(
        IReadOnlyDictionary<string, AuthenticodeSignatureResult> results) : IAuthenticodeSignatureVerifier
    {
        public AuthenticodeSignatureResult Verify(string filePath)
            => results[Path.GetFileName(filePath)];
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
