using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoRecon.Core.Models;

namespace RepoRecon.Core.Adapters;

public interface IServerAdapterFactory
{
    IServerAdapter GetAdapter(ServerApiKind apiKind);
}

public class ServerAdapterFactory : IServerAdapterFactory
{
    private readonly HttpClient _httpClient;
    private readonly ILoggerFactory _loggerFactory;

    public ServerAdapterFactory(HttpClient? httpClient = null, ILoggerFactory? loggerFactory = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public IServerAdapter GetAdapter(ServerApiKind apiKind)
    {
        return apiKind switch
        {
            ServerApiKind.GitLab => new GitLabAdapter(_httpClient, _loggerFactory.CreateLogger<GitLabAdapter>()),
            ServerApiKind.GitHub => new GitHubAdapter(_httpClient, _loggerFactory.CreateLogger<GitHubAdapter>()),
            ServerApiKind.Gitea => new GiteaAdapter(_httpClient, _loggerFactory.CreateLogger<GiteaAdapter>()),
            _ => throw new NotSupportedException($"Unsupported ServerApiKind: {apiKind}")
        };
    }

    public static string MapHttpStatusToErrorCategory(System.Net.HttpStatusCode statusCode)
    {
        return Git.ErrorClassifier.ClassifyHttpStatus(statusCode).ToString();
    }
}
