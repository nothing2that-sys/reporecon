using RepoRecon.Core.Urls;
using Xunit;

namespace RepoRecon.Core.Tests;

public class RemoteUrlNormalizerTests
{
    [Theory]
    [InlineData("ssh://git@host:2222/g/p.git", "host:2222/g/p")]
    [InlineData("git@host:g/p.git", "host:22/g/p")]
    [InlineData("https://host/g/P", "host:443/g/p")]
    [InlineData("https://host/g/p.git", "host:443/g/p")]
    [InlineData("https://user:token@host/g/p.git", "host:443/g/p")]
    [InlineData("http://host:8080/g/p", "host:8080/g/p")]
    public void Normalize_Section6_4_RequiredCases_ReturnsExpectedKey(string inputUrl, string expectedKey)
    {
        var actualKey = RemoteUrlNormalizer.Normalize(inputUrl);
        Assert.Equal(expectedKey, actualKey);
    }

    [Fact]
    public void Normalize_KoreanPath_HttpsAndScp_ProduceSameNormalizedKey()
    {
        var httpsUrl = "https://gitlab.test/그룹/프로젝트.git";
        var scpUrl = "git@gitlab.test:그룹/프로젝트.git";

        var httpsKey = RemoteUrlNormalizer.Normalize(httpsUrl);
        var scpKey = RemoteUrlNormalizer.Normalize(scpUrl);

        Assert.Equal("gitlab.test:443/그룹/프로젝트", httpsKey);
        Assert.Equal("gitlab.test:22/그룹/프로젝트", scpKey);
        // Note: hosts and paths are identical, differing only by standard port per protocol
    }

    [Theory]
    [InlineData(@"D:\repos\my-project")]
    [InlineData(@"C:/repos/another-project.git")]
    [InlineData(@"file:///D:/repos/local-repo.git")]
    [InlineData(@"\\server\share\repo.git")]
    [InlineData(@"./local/repo")]
    [InlineData(null)]
    [InlineData("")]
    public void Normalize_LocalOrEmptyPath_ReturnsNull(string? localPath)
    {
        var result = RemoteUrlNormalizer.Normalize(localPath);
        Assert.Null(result);
    }

    [Fact]
    public void Normalize_WithHostAliasTable_MapsAliasToCanonical()
    {
        var aliasTable = new HostAliasTable();
        aliasTable.SetAlias("192.0.2.10", "gitlab.test");

        var normalized1 = RemoteUrlNormalizer.Normalize("https://192.0.2.10/group/project.git", aliasTable);
        var normalized2 = RemoteUrlNormalizer.Normalize("https://gitlab.test/group/project.git", aliasTable);

        Assert.Equal("gitlab.test:443/group/project", normalized1);
        Assert.Equal("gitlab.test:443/group/project", normalized2);
    }

    [Theory]
    [InlineData("https://user:secret-token_123@gitlab.test/group/project.git", "https://***:***@gitlab.test/group/project.git")]
    [InlineData("https://onlytoken@gitlab.test/group/project.git", "https://***:***@gitlab.test/group/project.git")]
    [InlineData("ssh://git:token123@gitlab.test:2222/group/project.git", "ssh://***:***@gitlab.test:2222/group/project.git")]
    public void MaskCredentials_MasksUserAndToken(string input, string expected)
    {
        var masked = RemoteUrlNormalizer.MaskCredentials(input);
        Assert.Equal(expected, masked);
        Assert.DoesNotContain("secret-token_123", masked);
        Assert.DoesNotContain("onlytoken", masked);
        Assert.DoesNotContain("token123", masked);
    }
}
