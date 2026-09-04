using Fortiq.Scheduling;

namespace Fortiq.Scheduling.Tests;

/// <summary>
/// When a schedule comes due. The awkward cases are the point: a wall-clock time collides with
/// daylight saving twice a year, and a machine that was off owes a backup rather than a queue of
/// them.
/// </summary>
public sealed class RecurrenceTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    [Fact]
    public void AnIntervalIsAnIntervalWhateverTheLocalClockSays()
    {
        var recurrence = new EveryInterval(TimeSpan.FromHours(6));
        var after = new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.FromHours(1));

        // Across the spring-forward night: six hours later is six hours later, even though the local
        // clock advanced by seven.
        Assert.Equal(after.AddHours(6), recurrence.NextOccurrence(after));
    }

    [Fact]
    public void ADailyTimeIsTheSameWallClockTimeAcrossADaylightSavingChange()
    {
        var recurrence = new DailyAt(new TimeOnly(9, 0), Berlin);
        var beforeChange = new DateTimeOffset(2026, 3, 28, 10, 0, 0, TimeSpan.FromHours(1));

        var next = recurrence.NextOccurrence(beforeChange);

        // The clock moves forward that night; nine in the morning is still nine in the morning.
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 9, 0, 0, TimeSpan.FromHours(2)), next);
        Assert.Equal(9, TimeZoneInfo.ConvertTime(next, Berlin).Hour);
    }

    [Fact]
    public void ATimeThatDoesNotExistThatDayRunsAtTheFirstMomentThatDoes()
    {
        // In Berlin on 29 March 2026 the clock jumps from 02:00 to 03:00, so 02:30 never happens.
        var recurrence = new DailyAt(new TimeOnly(2, 30), Berlin);

        var next = recurrence.NextOccurrence(new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.FromHours(1)));

        // Skipping the day would mean no backup that night; running at 03:00 keeps the schedule.
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.FromHours(2)), next);
    }

    [Fact]
    public void ATimeThatHappensTwiceThatDayRunsOnce()
    {
        // In Berlin on 25 October 2026 the clock falls back from 03:00 to 02:00, so 02:30 happens
        // twice: once at UTC+2 and once at UTC+1.
        var recurrence = new DailyAt(new TimeOnly(2, 30), Berlin);

        var next = recurrence.NextOccurrence(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(2)));

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2)), next);

        // The second 02:30 is not a second occurrence: the following one is the next day.
        var following = recurrence.NextOccurrence(next);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 2, 30, 0, TimeSpan.FromHours(1)), following);
    }

    [Fact]
    public void AWeeklyScheduleOnlyComesDueOnItsDays()
    {
        var recurrence = new DailyAt(new TimeOnly(3, 0), Berlin, [DayOfWeek.Saturday, DayOfWeek.Sunday]);
        var wednesday = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.FromHours(2));

        var next = recurrence.NextOccurrence(wednesday);

        Assert.Equal(DayOfWeek.Saturday, TimeZoneInfo.ConvertTime(next, Berlin).DayOfWeek);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 3, 0, 0, TimeSpan.FromHours(2)), next);
        Assert.Equal(DayOfWeek.Sunday, TimeZoneInfo.ConvertTime(recurrence.NextOccurrence(next), Berlin).DayOfWeek);
    }

    [Fact]
    public void TheNextOccurrenceIsAlwaysAfterTheMomentAskedAbout()
    {
        var daily = new DailyAt(new TimeOnly(3, 0), Berlin);
        var exactlyOnTime = new DateTimeOffset(2026, 9, 2, 3, 0, 0, TimeSpan.FromHours(2));

        // Otherwise a schedule that just ran would be due again on the same instant, forever.
        Assert.True(daily.NextOccurrence(exactlyOnTime) > exactlyOnTime);
        Assert.Equal(new DateTimeOffset(2026, 9, 3, 3, 0, 0, TimeSpan.FromHours(2)), daily.NextOccurrence(exactlyOnTime));
    }

    [Fact]
    public void ARecurrenceThatCannotComeDueSaysSoInsteadOfLooping()
    {
        Assert.Throws<InvalidOperationException>(() => new EveryInterval(TimeSpan.Zero).NextOccurrence(DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(
            () => new DailyAt(new TimeOnly(3, 0), Berlin, []).NextOccurrence(DateTimeOffset.UtcNow));
    }
}
