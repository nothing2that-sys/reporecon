using System.Net;
using System.Text.Json;
using RepoRecon.Core.Adapters;
using RepoRecon.Core.Models;
using Xunit;

namespace RepoRecon.Core.Tests;

public class AdapterTests
{
    #region 1. GitLab Adapter Tests (§10 Criteria 7, 10, 11)

    [Fact]
    public void GitLabAdapter_ParsesProjectElement_TolerantlyWithMissingFields()
    {
        // Item with missing namespace object and missing http_url_to_repo (only web_url)
        var json = @"{
            ""name"": ""partial-repo"",
            ""path"": ""partial-repo"",
            ""web_url"": ""https://gitlab.example.com/team/partial-repo"",
            ""default_branch"": ""main""
        }";

        using var doc = JsonDocument.Parse(json);
        var parsed = GitLabAdapter.ParseProjectElement(doc.RootElement);

        Assert.NotNull(parsed);
        Assert.Equal("partial-repo", parsed.Name);
        Assert.Equal("https://gitlab.example.com/team/partial-repo.git", parsed.CloneUrl);
        Assert.Equal("main", parsed.DefaultBranch);
        Assert.False(parsed.IsArchived);
    }

    [Fact]
    public void GitLabAdapter_ParsesGroupRepo_CorrectlySplittingNamespace()
    {
        // Item from group with nested sub-groups (§10 Criterion 11)
        var json = @"{
            ""id"": 1234,
            ""name"": ""core-backend"",
            ""path_with_namespace"": ""enterprise/group-a/subgroup-b/core-backend"",
            ""http_url_to_repo"": ""https://gitlab.demo.test/enterprise/group-a/subgroup-b/core-backend.git"",
            ""default_branch"": ""master"",
            ""archived"": true
        }";

        using var doc = JsonDocument.Parse(json);
        var parsed = GitLabAdapter.ParseProjectElement(doc.RootElement);

        Assert.NotNull(parsed);
        Assert.Equal("enterprise/group-a/subgroup-b", parsed.Namespace);
        Assert.Equal("core-backend", parsed.Name);
        Assert.Equal("https://gitlab.demo.test/enterprise/group-a/subgroup-b/core-backend.git", parsed.CloneUrl);
        Assert.Equal("master", parsed.DefaultBranch);
        Assert.True(parsed.IsArchived);
    }

    [Fact]
    public async Task GitLabAdapter_FetchesPaginatedCatalog_UsingXNextPageHeader()
    {
        var handler = new MockHttpMessageHandler((request, page) =>
        {
            if (request.RequestUri!.Query.EndsWith("page=1"))
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"[
                        {""name"":""p1"", ""path_with_namespace"":""ns/p1"", ""http_url_to_repo"":""https://gitlab.com/ns/p1.git""},
                        {""name"":""p2"", ""path_with_namespace"":""ns/p2"", ""http_url_to_repo"":""https://gitlab.com/ns/p2.git""}
                    ]")
                };
                resp.Headers.Add("X-Next-Page", "2");
                return resp;
            }
            else
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"[
                        {""name"":""p3"", ""path_with_namespace"":""ns/p3"", ""http_url_to_repo"":""https://gitlab.com/ns/p3.git""}
                    ]")
                };
                resp.Headers.Add("X-Next-Page", "");
                return resp;
            }
        });

        var client = new HttpClient(handler);
        var adapter = new GitLabAdapter(client);
        var profile = new ServerProfile(1, "gitlab.com", ServerApiKind.GitLab, new List<string>(), "tokenref");

        var repos = await adapter.FetchCatalogAsync(profile, "fake-token");

        Assert.Equal(3, repos.Count);
        Assert.Equal("p1", repos[0].Name);
        Assert.Equal("p2", repos[1].Name);
        Assert.Equal("p3", repos[2].Name);
    }

    #endregion

    #region 2. GitHub Adapter Tests (§10 Criteria 8, 10, 12)

    [Fact]
    public void GitHubAdapter_ParsesLinkHeader_ForRelNext()
    {
        var response = new HttpResponseMessage();
        response.Headers.Add("Link", "<https://api.github.com/user/repos?page=2&per_page=100>; rel=\"next\", <https://api.github.com/user/repos?page=5&per_page=100>; rel=\"last\"");

        var nextUrl = GitHubAdapter.ExtractNextLink(response.Headers);

        Assert.Equal("https://api.github.com/user/repos?page=2&per_page=100", nextUrl);
    }

    [Fact]
    public void GitHubAdapter_ParsesRepoElement_TolerantlyWithMissingFields()
    {
        // Minimal json with missing clone_url (only html_url)
        var json = @"{
            ""name"": ""lib-utils"",
            ""full_name"": ""myorg/lib-utils"",
            ""html_url"": ""https://github.com/myorg/lib-utils""
        }";

        using var doc = JsonDocument.Parse(json);
        var parsed = GitHubAdapter.ParseRepoElement(doc.RootElement);

        Assert.NotNull(parsed);
        Assert.Equal("myorg", parsed.Namespace);
        Assert.Equal("lib-utils", parsed.Name);
        Assert.Equal("https://github.com/myorg/lib-utils.git", parsed.CloneUrl);
        Assert.Null(parsed.DefaultBranch);
        Assert.False(parsed.IsArchived);
    }

    [Fact]
    public async Task GitHubAdapter_TraversesUserAndOrgRepos_AndDeduplicates()
    {
        var handler = new MockHttpMessageHandler((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("/user/repos"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"[
                        {""name"":""user-repo"", ""full_name"":""user/user-repo"", ""clone_url"":""https://github.com/user/user-repo.git""},
                        {""name"":""org-repo"", ""full_name"":""my-org/org-repo"", ""clone_url"":""https://github.com/my-org/org-repo.git""}
                    ]")
                };
            }
            if (uri.Contains("/user/orgs"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"[
                        {""login"":""my-org""}
                    ]")
                };
            }
            if (uri.Contains("/orgs/my-org/repos"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"[
                        {""name"":""org-repo"", ""full_name"":""my-org/org-repo"", ""clone_url"":""https://github.com/my-org/org-repo.git""},
                        {""name"":""org-private-repo"", ""full_name"":""my-org/org-private-repo"", ""clone_url"":""https://github.com/my-org/org-private-repo.git""}
                    ]")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var client = new HttpClient(handler);
        var adapter = new GitHubAdapter(client);
        var profile = new ServerProfile(2, "github.com", ServerApiKind.GitHub, new List<string>(), "tokenref");

        var repos = await adapter.FetchCatalogAsync(profile, "fake-gh-token");

        Assert.Equal(3, repos.Count);
        Assert.Contains(repos, r => r.Name == "user-repo");
        Assert.Contains(repos, r => r.Name == "org-repo");
        Assert.Contains(repos, r => r.Name == "org-private-repo");
    }

    #endregion

    #region 3. Gitea Adapter Tests (§10 Criteria 9, 10)

    [Fact]
    public void GiteaAdapter_ParsesRepoElement_Tolerantly()
    {
        var json = @"{
            ""name"": ""gitea-app"",
            ""full_name"": ""team/gitea-app"",
            ""clone_url"": ""https://gitea.test/team/gitea-app.git"",
            ""default_branch"": ""develop"",
            ""archived"": false
        }";

        using var doc = JsonDocument.Parse(json);
        var parsed = GiteaAdapter.ParseGiteaRepoElement(doc.RootElement);

        Assert.NotNull(parsed);
        Assert.Equal("team", parsed.Namespace);
        Assert.Equal("gitea-app", parsed.Name);
        Assert.Equal("https://gitea.test/team/gitea-app.git", parsed.CloneUrl);
        Assert.Equal("develop", parsed.DefaultBranch);
        Assert.False(parsed.IsArchived);
    }

    [Fact]
    public async Task GiteaAdapter_FetchesPaginatedCatalog_UsingXTotalCount()
    {
        var handler = new MockHttpMessageHandler((request, _) =>
        {
            if (request.RequestUri!.Query.EndsWith("page=1"))
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"[
                        {""name"":""g1"", ""full_name"":""tea/g1"", ""clone_url"":""https://gitea.io/tea/g1.git""},
                        {""name"":""g2"", ""full_name"":""tea/g2"", ""clone_url"":""https://gitea.io/tea/g2.git""}
                    ]")
                };
                resp.Headers.Add("X-Total-Count", "3");
                return resp;
            }
            else
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"[
                        {""name"":""g3"", ""full_name"":""tea/g3"", ""clone_url"":""https://gitea.io/tea/g3.git""}
                    ]")
                };
                resp.Headers.Add("X-Total-Count", "3");
                return resp;
            }
        });

        var client = new HttpClient(handler);
        var adapter = new GiteaAdapter(client);
        var profile = new ServerProfile(3, "gitea.io", ServerApiKind.Gitea, new List<string>(), "tokenref");

        var repos = await adapter.FetchCatalogAsync(profile, "fake-gitea-token");

        Assert.Equal(3, repos.Count);
        Assert.Equal("g1", repos[0].Name);
        Assert.Equal("g2", repos[1].Name);
        Assert.Equal("g3", repos[2].Name);
    }

    #endregion

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _handler;
        private int _callCount;

        public MockHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _callCount);
            var response = _handler(request, count);
            return Task.FromResult(response);
        }
    }
}
