using RepoRecon.Core.Pull;
using Xunit;

namespace RepoRecon.Core.Tests;

public class AutoPullScheduleCalculatorTests
{
    private static readonly IReadOnlyList<TimeOnly> OneSlotAt9 = new[] { new TimeOnly(9, 0) };
    private static readonly IReadOnlyList<TimeOnly> TwoSlots = new[] { new TimeOnly(9, 0), new TimeOnly(20, 0) };

    [Fact]
    public void NoScheduleTimes_NeverDue()
    {
        var now = new DateTime(2026, 9, 2, 9, 0, 0);
        Assert.False(AutoPullScheduleCalculator.IsSlotDue(now, Array.Empty<TimeOnly>(), null));
    }

    [Fact]
    public void SlotNotYetReachedToday_NotDue()
    {
        var now = new DateTime(2026, 9, 2, 8, 59, 0);
        Assert.False(AutoPullScheduleCalculator.IsSlotDue(now, OneSlotAt9, null));
    }

    [Fact]
    public void SlotJustReached_NeverRunBefore_IsDue()
    {
        var now = new DateTime(2026, 9, 2, 9, 0, 30);
        Assert.True(AutoPullScheduleCalculator.IsSlotDue(now, OneSlotAt9, null));
    }

    [Fact]
    public void SlotAlreadyRunToday_NotDueAgain()
    {
        var now = new DateTime(2026, 9, 2, 9, 5, 0);
        var lastRun = new DateTime(2026, 9, 2, 9, 0, 30); // already ran right after the slot
        Assert.False(AutoPullScheduleCalculator.IsSlotDue(now, OneSlotAt9, lastRun));
    }

    [Fact]
    public void MissedRunWhileOff_CatchesUpOnce()
    {
        // App was off from before 09:00 until 10:00 -- the 09:00 slot was missed.
        var now = new DateTime(2026, 9, 2, 10, 0, 0);
        var lastRun = new DateTime(2026, 9, 1, 20, 5, 0); // yesterday's 20:00 run
        Assert.True(AutoPullScheduleCalculator.IsSlotDue(now, OneSlotAt9, lastRun));
    }

    [Fact]
    public void MultipleMissedSlots_CatchUpFiresOnlyOnce_NotOncePerMissedSlot()
    {
        // Both 09:00 and would-be-later slots are in the past relative to "now", but once the
        // caller updates lastRunLocal to the catch-up run's own timestamp, no further slot fires
        // the same tick/day -- simulating the caller's actual update-after-run pattern.
        var now = new DateTime(2026, 9, 2, 21, 0, 0);
        var lastRun = new DateTime(2026, 9, 1, 20, 5, 0);

        Assert.True(AutoPullScheduleCalculator.IsSlotDue(now, TwoSlots, lastRun));

        // Simulate: scheduler just ran the catch-up and stamped lastRunLocal = now.
        var afterCatchUp = now;
        Assert.False(AutoPullScheduleCalculator.IsSlotDue(now, TwoSlots, afterCatchUp));
    }

    [Fact]
    public void SecondSlotSameDay_FiresIndependently()
    {
        var lastRun = new DateTime(2026, 9, 2, 9, 0, 30); // 09:00 slot already handled today
        var now = new DateTime(2026, 9, 2, 20, 0, 15);
        Assert.True(AutoPullScheduleCalculator.IsSlotDue(now, TwoSlots, lastRun));
    }
}
