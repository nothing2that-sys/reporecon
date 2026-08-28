using RepoRecon.Core.Git;
using RepoRecon.Core.Models;
using Xunit;

namespace RepoRecon.Core.Tests;

public class GitStatusParserTests
{
    [Fact]
    public void Parse_NormalUpToDateOutput_ParsesCorrectly()
    {
        var output = @"# branch.oid 1234567890abcdef1234567890abcdef12345678
# branch.head main
# branch.upstream origin/main
# branch.ab +0 -0";

        var status = GitStatusParser.Parse(1, output, hasRemote: true);

        Assert.Equal("main", status.Branch);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(0, status.Ahead);
        Assert.Equal(0, status.Behind);
        Assert.Equal(0, status.DirtyCount);
        Assert.Equal(0, status.ConflictedCount);
        Assert.Equal("1234567890abcdef1234567890abcdef12345678", status.HeadHash);
        Assert.Equal(RepoStateCode.UpToDate, status.StateCode);
    }

    [Fact]
    public void Parse_DirtyAndUntracked_OnlyModifiedFilesCountedInDirty()
    {
        var output = @"# branch.oid 1234567890abcdef1234567890abcdef12345678
# branch.head feature/test
# branch.upstream origin/feature/test
# branch.ab +3 -0
1 .M N... 100644 100644 100644 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 file1.txt
2 R. N... 100644 100644 100644 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 R100 file2.txt file2_renamed.txt
? untracked_file.txt";

        var status = GitStatusParser.Parse(1, output, hasRemote: true);

        Assert.Equal("feature/test", status.Branch);
        Assert.Equal("origin/feature/test", status.Upstream);
        Assert.Equal(3, status.Ahead);
        Assert.Equal(0, status.Behind);
        Assert.Equal(2, status.DirtyCount); // 1 and 2 only (untracked excluded)
        Assert.Equal(0, status.ConflictedCount);
        Assert.Equal(RepoStateCode.Ahead, status.StateCode); // Sync state is Ahead
    }

    [Fact]
    public void Parse_UnmergedConflicts_CountsInConflictedCount()
    {
        var output = @"# branch.oid 1234567890abcdef1234567890abcdef12345678
# branch.head main
# branch.upstream origin/main
# branch.ab +1 -1
u UU N... 100644 100644 100644 100644 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 conflict_file.txt
1 .M N... 100644 100644 100644 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 normal_dirty.txt";

        var status = GitStatusParser.Parse(1, output, hasRemote: true);

        Assert.Equal(1, status.DirtyCount);
        Assert.Equal(1, status.ConflictedCount);
        Assert.Equal(RepoStateCode.Conflicted, status.StateCode);
    }

    [Fact]
    public void Parse_UpstreamGone_WhenUpstreamExistsButAbMissing()
    {
        // When upstream branch is deleted on remote, git status outputs branch.upstream but NO branch.ab
        var output = @"# branch.oid 1234567890abcdef1234567890abcdef12345678
# branch.head main
# branch.upstream origin/main";

        var status = GitStatusParser.Parse(1, output, hasRemote: true);

        Assert.Equal("main", status.Branch);
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(RepoStateCode.UpstreamGone, status.StateCode);
    }

    [Fact]
    public void Parse_NoUpstream_ReturnsNoUpstreamState()
    {
        var output = @"# branch.oid 1234567890abcdef1234567890abcdef12345678
# branch.head local-branch
1 .M N... 100644 100644 100644 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 e69de29bb2d1d6434b8b29ae775ad8c2e48c5391 dirty.txt";

        var status = GitStatusParser.Parse(1, output, hasRemote: true);

        Assert.Equal("local-branch", status.Branch);
        Assert.Null(status.Upstream);
        Assert.Equal(1, status.DirtyCount);
        Assert.Equal(RepoStateCode.NoUpstream, status.StateCode); // Sync state is NoUpstream, while DirtyCount=1
    }

    [Fact]
    public void Parse_DetachedHead_ReturnsDetachedHeadState()
    {
        var output = @"# branch.oid 1234567890abcdef1234567890abcdef12345678
# branch.head (detached)";

        var status = GitStatusParser.Parse(1, output, hasRemote: true);

        Assert.Equal("(detached)", status.Branch);
        Assert.Equal(RepoStateCode.DetachedHead, status.StateCode);
    }

    [Fact]
    public void Parse_UntrackedRepo_ReturnsUntrackedState()
    {
        var output = @"# branch.oid 1234567890abcdef1234567890abcdef12345678
# branch.head main";

        var status = GitStatusParser.Parse(1, output, hasRemote: false);

        Assert.Equal(RepoStateCode.Untracked, status.StateCode);
    }
}
