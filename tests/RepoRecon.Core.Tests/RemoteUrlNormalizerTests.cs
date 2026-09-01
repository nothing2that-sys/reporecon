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

    [Fact]
    public void Normalize_WithPortSpecificAlias_ReplacesBothHostAndPort()
    {
        // §2.3 v4.1: 실환경에서 로컬은 IP:port로, 서버 clone_url은 다른 host:80으로 왔다.
        var aliasTable = new HostAliasTable();
        aliasTable.SetAlias("gitlab:80", "192.0.2.9:10012");

        var fromCloneUrl = RemoteUrlNormalizer.Normalize("http://gitlab/group/project.git", aliasTable);

        Assert.Equal("192.0.2.9:10012/group/project", fromCloneUrl);
    }

    [Fact]
    public void Normalize_WithBareHostAlias_IgnoresIncomingPortAndUsesCanonicalPort()
    {
        var aliasTable = new HostAliasTable();
        aliasTable.SetAlias("gitlab", "192.0.2.9:10012");

        // 포트가 다른 두 URL 모두 host만으로 매칭되어 같은 canonical authority로 치환된다
        var viaHttp = RemoteUrlNormalizer.Normalize("http://gitlab:8080/group/project.git", aliasTable);
        var viaSsh = RemoteUrlNormalizer.Normalize("git@gitlab:group/project.git", aliasTable);

        Assert.Equal("192.0.2.9:10012/group/project", viaHttp);
        Assert.Equal("192.0.2.9:10012/group/project", viaSsh);
    }

    [Theory]
    [InlineData("http://192.0.2.9:10012/", "192.0.2.9:10012")]
    [InlineData("http://gitlab/", "gitlab:80")]
    [InlineData("https://github.com/", "github.com:443")]
    [InlineData("git@gitlab.test:group/project.git", "gitlab.test:22")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void ExtractAuthority_ReturnsHostAndPortOnly(string? url, string? expected)
    {
        Assert.Equal(expected, RemoteUrlNormalizer.ExtractAuthority(url));
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
