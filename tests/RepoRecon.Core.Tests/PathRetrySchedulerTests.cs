using RepoRecon.Core.Pull;
using Xunit;

namespace RepoRecon.Core.Tests;

public class PathRetrySchedulerTests
{
    [Fact]
    public void ScheduledRetry_NotDueImmediately()
    {
        var scheduler = new PathRetryScheduler();
        var now = new DateTime(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc);
        scheduler.Schedule(repoId: 1, nowUtc: now);

        var due = scheduler.ConsumeDue(now);

        Assert.Empty(due);
        Assert.Equal(1, scheduler.PendingCount);
    }

    [Fact]
    public void ScheduledRetry_DueAfterTenMinutes()
    {
        var scheduler = new PathRetryScheduler();
        var scheduledAt = new DateTime(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc);
        scheduler.Schedule(repoId: 1, nowUtc: scheduledAt);

        var tenMinutesLater = scheduledAt.AddMinutes(10);
        var due = scheduler.ConsumeDue(tenMinutesLater);

        Assert.Single(due);
        Assert.Equal(1, due[0]);
    }

    [Fact]
    public void ScheduledRetry_NotDueBeforeTenMinutes()
    {
        var scheduler = new PathRetryScheduler();
        var scheduledAt = new DateTime(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc);
        scheduler.Schedule(repoId: 1, nowUtc: scheduledAt);

        var justUnder = scheduledAt.AddMinutes(9).AddSeconds(59);
        var due = scheduler.ConsumeDue(justUnder);

        Assert.Empty(due);
    }

    [Fact]
    public void ConsumedRetry_IsNotReturnedAgain_EnforcingOnlyOneRetry()
    {
        var scheduler = new PathRetryScheduler();
        var scheduledAt = new DateTime(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc);
        scheduler.Schedule(repoId: 1, nowUtc: scheduledAt);

        var dueTime = scheduledAt.AddMinutes(10);
        var firstConsume = scheduler.ConsumeDue(dueTime);
        Assert.Single(firstConsume);

        // Calling again later must not re-return the same repo -- "10분 후 1회, 그 이후는
        // 재시도하지 않는다" is enforced by removal, not by a due-time bound alone.
        var muchLater = dueTime.AddHours(5);
        var secondConsume = scheduler.ConsumeDue(muchLater);
        Assert.Empty(secondConsume);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public void MultipleRepos_OnlyDueOnesAreConsumed()
    {
        var scheduler = new PathRetryScheduler();
        var t0 = new DateTime(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc);
        scheduler.Schedule(repoId: 1, nowUtc: t0);
        scheduler.Schedule(repoId: 2, nowUtc: t0.AddMinutes(5)); // scheduled 5 min later -> due 5 min later than repo 1

        // At t0 + 10min, only repo 1 (due at t0+10) is due; repo 2 is due at t0+15.
        var due = scheduler.ConsumeDue(t0.AddMinutes(10));

        Assert.Single(due);
        Assert.Equal(1, due[0]);
        Assert.Equal(1, scheduler.PendingCount); // repo 2 still pending
    }

    [Fact]
    public void NoRetriesScheduled_ConsumeDueReturnsEmpty()
    {
        var scheduler = new PathRetryScheduler();
        var due = scheduler.ConsumeDue(DateTime.UtcNow);
        Assert.Empty(due);
    }
}
