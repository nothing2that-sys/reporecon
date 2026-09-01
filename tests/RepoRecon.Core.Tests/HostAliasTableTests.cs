using RepoRecon.Core.Models;
using RepoRecon.Core.Urls;
using Xunit;

namespace RepoRecon.Core.Tests;

public class HostAliasTableTests
{
    [Fact]
    public void ReloadFromProfiles_LoadsAliasesKeyedToProfileHostAuthority()
    {
        var profile = new ServerProfile(
            Id: 1,
            Host: "http://192.0.2.9:10012/",
            ApiKind: ServerApiKind.GitLab,
            Aliases: new List<string> { "gitlab:80", "gitlab" },
            TokenRef: "ref");

        var table = new HostAliasTable();
        table.ReloadFromProfiles(new[] { profile });

        var resolved = table.ResolveAuthority("gitlab", 80);
        Assert.Equal(("192.0.2.9", 10012), resolved);
    }

    [Fact]
    public void ReloadFromProfiles_ClearsPreviousAliasesBeforeReloading()
    {
        var table = new HostAliasTable();
        table.SetAlias("stale-host", "stale-canonical:1");

        table.ReloadFromProfiles(Array.Empty<ServerProfile>());

        var resolved = table.ResolveAuthority("stale-host", 443);
        Assert.Equal(("stale-host", 443), resolved);
    }

    [Fact]
    public void ReloadFromProfiles_SkipsProfileWithUnparsableHost()
    {
        var profile = new ServerProfile(
            Id: 1,
            Host: "not a url",
            ApiKind: ServerApiKind.Gitea,
            Aliases: new List<string> { "some-alias" },
            TokenRef: "ref");

        var table = new HostAliasTable();
        table.ReloadFromProfiles(new[] { profile });

        var resolved = table.ResolveAuthority("some-alias", 443);
        Assert.Equal(("some-alias", 443), resolved);
    }
}
