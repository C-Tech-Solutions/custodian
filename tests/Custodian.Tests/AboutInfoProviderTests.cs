using Custodian.App.Services;

namespace Custodian.Tests;

public sealed class AboutInfoProviderTests
{
    [Fact]
    public void Create_NormalizesVersionAndUsesRepositoryUrl()
    {
        var about = AboutInfoProvider.Create("1.5.4+76286e9");

        Assert.Equal("1.5.4", about.Version);
        Assert.Equal("https://github.com/ctech1313/custodian", about.RepositoryUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+build")]
    public void Create_UsesUnknownWhenVersionIsUnavailable(string? informationalVersion)
    {
        var about = AboutInfoProvider.Create(informationalVersion);

        Assert.Equal(AboutInfoProvider.UnknownVersion, about.Version);
        Assert.Equal(AboutInfoProvider.RepositoryUrl, about.RepositoryUrl);
    }
}
