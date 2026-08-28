using System.Text.RegularExpressions;
using RepoRecon.Core.Models;

namespace RepoRecon.Core.Git;

public static class GitEnvironmentCheck
{
    private static readonly Version MinimumRequiredVersion = new(2, 11, 0);
    private static readonly Regex VersionRegex = new(@"git\s+version\s+(?<version>\d+\.\d+\.\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static async Task<GitEnvironmentInfo> CheckAsync(IGitRunner gitRunner, CancellationToken cancellationToken = default)
    {
        var result = await gitRunner.ExecuteAsync(string.Empty, "--version", TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            var errorDetail = string.IsNullOrWhiteSpace(result.StandardError)
                ? "Git 실행 파일을 찾을 수 없습니다."
                : result.StandardError;

            return new GitEnvironmentInfo(
                IsAvailable: false,
                Version: null,
                VersionString: null,
                GitPath: null,
                ErrorMessage: $"Git 실행 파일을 찾을 수 없거나 실행하지 못했습니다. ({errorDetail}) 시스템 환경변수 PATH에 Git이 설치되어 있는지 확인하세요.");
        }

        var output = result.StandardOutput.Trim();
        var match = VersionRegex.Match(output);

        if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out var parsedVersion))
        {
            return new GitEnvironmentInfo(
                IsAvailable: false,
                Version: null,
                VersionString: output,
                GitPath: null,
                ErrorMessage: $"Git 버전 문자열을 파싱할 수 없습니다: '{output}'");
        }

        if (parsedVersion < MinimumRequiredVersion)
        {
            return new GitEnvironmentInfo(
                IsAvailable: false,
                Version: parsedVersion,
                VersionString: output,
                GitPath: null,
                ErrorMessage: $"Git 버전이 요구사항({MinimumRequiredVersion}+)에 미달합니다. 발견된 버전: {parsedVersion} (최소 2.11.0 이상 필요 — porcelain=v2 지원 필수)");
        }

        return new GitEnvironmentInfo(
            IsAvailable: true,
            Version: parsedVersion,
            VersionString: output,
            GitPath: "git",
            ErrorMessage: null);
    }
}
