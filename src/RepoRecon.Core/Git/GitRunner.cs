using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RepoRecon.Core.Models;
using RepoRecon.Core.Urls;

namespace RepoRecon.Core.Git;

public class GitRunner : IGitRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly ILogger<GitRunner> _logger;
    private readonly string _gitBinary;

    public GitRunner(ILogger<GitRunner>? logger = null, string gitBinary = "git")
    {
        _logger = logger ?? NullLogger<GitRunner>.Instance;
        _gitBinary = gitBinary;
    }

    public async Task<GitCommandResult> ExecuteAsync(
        string workingDirectory,
        string arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        var stopwatch = Stopwatch.StartNew();

        // Enforce mandatory flags: quotepath=false, UTF-8 output
        var fullArguments = $"-c core.quotepath=false -c i18n.logOutputEncoding=UTF-8 {arguments}";

        var maskedArgs = RemoteUrlNormalizer.MaskCredentials(fullArguments);
        _logger.LogDebug("Executing git {Arguments} in {WorkingDirectory}", maskedArgs, workingDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = _gitBinary,
            Arguments = fullArguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        // Enforce prompt suppression (GIT_TERMINAL_PROMPT=0, SSH_ASKPASS="", GIT_ASKPASS="")
        startInfo.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.EnvironmentVariables["SSH_ASKPASS"] = string.Empty;
        startInfo.EnvironmentVariables["GIT_ASKPASS"] = string.Empty;

        using var process = new Process { StartInfo = startInfo };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        var stdoutTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                stdoutTcs.TrySetResult(true);
            }
            else
            {
                stdoutBuilder.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
            {
                stderrTcs.TrySetResult(true);
            }
            else
            {
                stderrBuilder.AppendLine(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                return new GitCommandResult(-1, string.Empty, "Failed to start git process.", stopwatch.Elapsed);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = new CancellationTokenSource(effectiveTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                await Task.WhenAll(
                    Task.WhenAny(stdoutTcs.Task, Task.Delay(1000, CancellationToken.None)),
                    Task.WhenAny(stderrTcs.Task, Task.Delay(1000, CancellationToken.None))
                ).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore kill errors on already terminated processes
                }

                stopwatch.Stop();
                var reason = timeoutCts.IsCancellationRequested ? "Execution timed out." : "Execution was canceled.";
                _logger.LogWarning("Git command {Arguments} aborted: {Reason}", maskedArgs, reason);

                var maskedOut = RemoteUrlNormalizer.MaskCredentials(stdoutBuilder.ToString().TrimEnd());
                return new GitCommandResult(-1, maskedOut, reason, stopwatch.Elapsed);
            }

            stopwatch.Stop();

            // Mask credentials at the return boundary (A-8)
            var rawStdout = stdoutBuilder.ToString().TrimEnd();
            var rawStderr = stderrBuilder.ToString().TrimEnd();

            var maskedStdout = RemoteUrlNormalizer.MaskCredentials(rawStdout);
            var maskedStderr = RemoteUrlNormalizer.MaskCredentials(rawStderr);

            _logger.LogDebug("Git command finished with exit code {ExitCode} in {Duration}ms", process.ExitCode, stopwatch.ElapsedMilliseconds);
            return new GitCommandResult(process.ExitCode, maskedStdout, maskedStderr, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Exception while executing git command {Arguments}", maskedArgs);
            return new GitCommandResult(-1, string.Empty, ex.Message, stopwatch.Elapsed);
        }
    }

    public async Task<GitEnvironmentInfo> CheckEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        return await GitEnvironmentCheck.CheckAsync(this, cancellationToken).ConfigureAwait(false);
    }
}
