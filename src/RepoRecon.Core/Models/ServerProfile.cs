namespace RepoRecon.Core.Models;

public enum ServerApiKind
{
    GitLab,
    GitHub,
    Gitea
}

public record ServerProfile(
    long Id,
    string Host,
    ServerApiKind ApiKind,
    IReadOnlyList<string> Aliases,
    string TokenRef,
    string? ApiVersion = null,
    DateTime CreatedAt = default,
    DateTime? LastSyncedAt = null);
