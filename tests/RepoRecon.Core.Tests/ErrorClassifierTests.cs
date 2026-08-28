using RepoRecon.Core.Git;
using RepoRecon.Core.Models;
using Xunit;

namespace RepoRecon.Core.Tests;

public class ErrorClassifierTests
{
    [Theory]
    // 1. Authentication
    [InlineData("fatal: Authentication failed for 'https://github.com/org/repo.git/'", ErrorCategory.Authentication)]
    [InlineData("fatal: could not read Username for 'https://github.com': terminal prompts disabled", ErrorCategory.Authentication)]
    [InlineData("fatal: could not read Password for 'https://gitlab.com': No such device or address", ErrorCategory.Authentication)]
    [InlineData("error: 401 Unauthorized", ErrorCategory.Authentication)]
    [InlineData("The requested URL returned error: 401", ErrorCategory.Authentication)]
    [InlineData("remote: HTTP Basic: Access denied. Token is expired.", ErrorCategory.Authentication)]
    [InlineData("fatal: Invalid username or password", ErrorCategory.Authentication)]
    [InlineData("remote: Bad credentials", ErrorCategory.Authentication)]
    [InlineData("git@github.com: Permission denied (publickey).", ErrorCategory.Authentication)]
    [InlineData("git@gitlab.com: Permission denied (publickey,gssapi-keyex,password).", ErrorCategory.Authentication)]

    // 2. Permission
    [InlineData("error: The requested URL returned error: 403", ErrorCategory.Permission)]
    [InlineData("fatal: 403 Forbidden", ErrorCategory.Permission)]
    [InlineData("remote: Permission to user/repo.git denied to user.", ErrorCategory.Permission)]
    [InlineData("fatal: Permission denied", ErrorCategory.Permission)]
    [InlineData("remote: Repository not found or access denied.", ErrorCategory.Permission)]
    [InlineData("remote: You are not allowed to push code to this project.", ErrorCategory.Permission)]
    [InlineData("fatal: write access required", ErrorCategory.Permission)]
    [InlineData("fatal: read access required", ErrorCategory.Permission)]

    // 3. Network
    [InlineData("fatal: Could not resolve host: github.com", ErrorCategory.Network)]
    [InlineData("fatal: Failed to connect to git.example.com port 443: Connection refused", ErrorCategory.Network)]
    [InlineData("fatal: Connection reset by peer", ErrorCategory.Network)]
    [InlineData("fatal: Network is unreachable", ErrorCategory.Network)]
    [InlineData("fatal: No route to host", ErrorCategory.Network)]
    [InlineData("fatal: unable to access: SSL certificate problem: self signed certificate", ErrorCategory.Network)]
    [InlineData("fatal: The remote end hung up unexpectedly", ErrorCategory.Network)]
    [InlineData("fatal: early EOF", ErrorCategory.Network)]
    [InlineData("error: RPC failed; curl 56 OpenSSL SSL_read: Connection was reset", ErrorCategory.Network)]

    // 4. Timeout
    [InlineData("fatal: Connection timed out after 30000 milliseconds", ErrorCategory.Timeout)]
    [InlineData("fatal: Operation timed out", ErrorCategory.Timeout)]
    [InlineData("error: RPC failed; curl 28 Operation timed out after 60000 milliseconds", ErrorCategory.Timeout)]
    [InlineData("Execution timed out", ErrorCategory.Timeout)]
    [InlineData("Operation was canceled.", ErrorCategory.Timeout)]

    // 5. Conflict
    [InlineData("fatal: Not possible to fast-forward, aborting.", ErrorCategory.Conflict)]
    [InlineData("fatal: non-fast-forward update rejected", ErrorCategory.Conflict)]
    [InlineData("CONFLICT (content): Merge conflict in src/App.xaml.cs", ErrorCategory.Conflict)]
    [InlineData("Automatic merge failed; fix conflicts and then commit the result.", ErrorCategory.Conflict)]
    [InlineData("error: Your local changes to the following files would be overwritten by merge:", ErrorCategory.Conflict)]
    [InlineData("error: Your local changes to the following files would be overwritten by checkout:", ErrorCategory.Conflict)]
    [InlineData("fatal: refusing to merge unrelated histories", ErrorCategory.Conflict)]

    // 6. Path / Local Filesystem
    [InlineData("fatal: cannot create directory at 'very/long/path/...': Filename too long", ErrorCategory.Path)]
    [InlineData("fatal: Unable to create 'D:/repo/.git/index.lock': File exists.", ErrorCategory.Path)]
    [InlineData("error: Another git process seems to be running in this repository", ErrorCategory.Path)]
    [InlineData("fatal: write error: No space left on device", ErrorCategory.Path)]
    [InlineData("fatal: cannot lock ref 'refs/heads/main': is at unexpected sha1", ErrorCategory.Path)]
    [InlineData("fatal: not a valid repository", ErrorCategory.Path)]
    [InlineData("error: Corrupt object file", ErrorCategory.Path)]
    [InlineData("fatal: cannot create directory at 'D:/repos/test': Permission denied: unable to create", ErrorCategory.Path)]

    // 7. Other
    [InlineData("fatal: something unexpected happened completely unknown", ErrorCategory.Other)]
    [InlineData("", ErrorCategory.Other)]
    [InlineData(null, ErrorCategory.Other)]
    public void Classify_ReturnsExpectedCategory(string? errorMessage, ErrorCategory expected)
    {
        var result = ErrorClassifier.Classify(errorMessage);
        Assert.Equal(expected, result);
        Assert.Equal(expected.ToString(), ErrorClassifier.ClassifyToString(errorMessage));
    }
}
