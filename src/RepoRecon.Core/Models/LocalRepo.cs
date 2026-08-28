namespace RepoRecon.Core.Models;

public record LocalRepo(
    long Id,
    string AbsPath,
    string? NormalizedKey = null,
    long? ParentRepoId = null,
    GitDirKind GitDirKind = GitDirKind.Normal,
    bool HasLfs = false,
    bool HasSubmodule = false,
    bool IsMissing = false,
    DateTime? MissingSince = null);
