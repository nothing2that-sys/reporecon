using RepoRecon.Core.Git;
using Xunit;

namespace RepoRecon.Core.Tests;

public class GitRunnerTests
{
    private readonly GitRunner _runner = new();

    [Fact]
    public async Task CheckEnvironmentAsync_WithInstalledGit_ReturnsValidVersion()
    {
        var info = await _runner.CheckEnvironmentAsync();

        Assert.True(info.IsAvailable);
        Assert.NotNull(info.Version);
        Assert.True(info.Version >= new Version(2, 11, 0));
        Assert.Null(info.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_VersionCommand_ReturnsSuccess()
    {
        var result = await _runner.ExecuteAsync(string.Empty, "--version");

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("git version", result.StandardOutput);
    }

    [Fact]
    public async Task ExecuteAsync_KoreanFilePath_PreservesKoreanCharactersWithoutOctalEncoding()
    {
        // Arrange: create a temporary directory with Korean characters
        var tempRoot = Path.Combine(Path.GetTempPath(), "RepoRecon_한글테스트_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);

        try
        {
            // Init git repo
            await _runner.ExecuteAsync(tempRoot, "init");
            var koreanFileName = "한글문서_테스트.txt";
            var filePath = Path.Combine(tempRoot, koreanFileName);
            await File.WriteAllTextAsync(filePath, "테스트 내용입니다.");

            // Act: run git status --porcelain=v2
            var result = await _runner.ExecuteAsync(tempRoot, "status --porcelain=v2");

            // Assert: verify the output contains the raw Korean file name, NOT escaped octal \355\225...
            Assert.True(result.IsSuccess);
            Assert.Contains(koreanFileName, result.StandardOutput);
            Assert.DoesNotContain(@"\355", result.StandardOutput);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch
                {
                    // Ignore cleanup failure on temp
                }
            }
        }
    }
}
