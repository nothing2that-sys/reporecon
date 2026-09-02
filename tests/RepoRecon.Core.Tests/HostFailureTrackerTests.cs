using RepoRecon.Core.Pull;
using Xunit;

namespace RepoRecon.Core.Tests;

public class HostFailureTrackerTests
{
    [Fact]
    public void SuccessDoesNotPause()
    {
        var tracker = new HostFailureTracker();
        Assert.Null(tracker.RecordResult("gitlab:80", wasAuthFailure: false));
        Assert.Equal(0, tracker.GetConsecutiveFailures("gitlab:80"));
    }

    [Fact]
    public void TwoConsecutiveFailures_DoesNotPauseYet()
    {
        var tracker = new HostFailureTracker();
        Assert.Null(tracker.RecordResult("gitlab:80", wasAuthFailure: true));
        Assert.Null(tracker.RecordResult("gitlab:80", wasAuthFailure: true));
        Assert.Equal(2, tracker.GetConsecutiveFailures("gitlab:80"));
    }

    [Fact]
    public void ThirdConsecutiveFailure_ReturnsHostToPause()
    {
        var tracker = new HostFailureTracker();
        tracker.RecordResult("gitlab:80", wasAuthFailure: true);
        tracker.RecordResult("gitlab:80", wasAuthFailure: true);
        var paused = tracker.RecordResult("gitlab:80", wasAuthFailure: true);
        Assert.Equal("gitlab:80", paused);
    }

    [Fact]
    public void SuccessInBetween_ResetsCounter()
    {
        var tracker = new HostFailureTracker();
        tracker.RecordResult("gitlab:80", wasAuthFailure: true);
        tracker.RecordResult("gitlab:80", wasAuthFailure: true);
        tracker.RecordResult("gitlab:80", wasAuthFailure: false); // recovered
        var paused = tracker.RecordResult("gitlab:80", wasAuthFailure: true);
        Assert.Null(paused); // back to count 1, not paused
        Assert.Equal(1, tracker.GetConsecutiveFailures("gitlab:80"));
    }

    [Fact]
    public void DifferentHosts_CountedSeparately()
    {
        var tracker = new HostFailureTracker();
        tracker.RecordResult("hostA", wasAuthFailure: true);
        tracker.RecordResult("hostA", wasAuthFailure: true);
        var pausedForB = tracker.RecordResult("hostB", wasAuthFailure: true);
        Assert.Null(pausedForB);
        Assert.Equal(2, tracker.GetConsecutiveFailures("hostA"));
        Assert.Equal(1, tracker.GetConsecutiveFailures("hostB"));
    }
}
