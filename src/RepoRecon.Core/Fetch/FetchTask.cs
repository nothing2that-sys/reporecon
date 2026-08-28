namespace RepoRecon.Core.Fetch;

public record FetchTask(
    long LocalRepoId,
    string AbsPath,
    string HostKey,
    int Priority,
    DateTime EnqueuedAt
);
