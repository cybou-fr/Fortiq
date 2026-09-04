namespace Fortiq.Scheduling;

/// <summary>
/// When a schedule is due. Two kinds, because they answer different questions: an interval says "no
/// more than this long without a backup", a wall-clock time says "at this time of day, whatever the
/// clock did in between".
/// </summary>
public abstract record Recurrence
{
    /// <summary>
    /// The first moment at or after <paramref name="after"/> when this recurrence is due. Always
    /// strictly later than <paramref name="after"/>, so a schedule cannot fire twice on one instant.
    /// </summary>
    public abstract DateTimeOffset NextOccurrence(DateTimeOffset after);
}

/// <summary>
/// Due every <paramref name="Period"/> since the previous run. Unaffected by clock changes: an hour
/// is an hour whatever the local time is called.
/// </summary>
public sealed record EveryInterval(TimeSpan Period) : Recurrence
{
    public override DateTimeOffset NextOccurrence(DateTimeOffset after) =>
        Period > TimeSpan.Zero
            ? after + Period
            : throw new InvalidOperationException("An interval recurrence needs a positive period.");
}

/// <summary>
/// Due at a wall-clock time of day, in a stated time zone, on the stated days.
/// </summary>
/// <remarks>
/// Wall-clock times collide with daylight saving twice a year, and both cases are decided here
/// rather than left to chance. When the clock springs forward the chosen time may not exist that
/// day: the schedule becomes due at the first moment that does exist, rather than being skipped for
/// a day. When the clock falls back the time happens twice: the first occurrence is used, so a daily
/// schedule runs once a day.
/// </remarks>
public sealed record DailyAt(TimeOnly TimeOfDay, TimeZoneInfo TimeZone, IReadOnlyList<DayOfWeek>? Days = null) : Recurrence
{
    public override DateTimeOffset NextOccurrence(DateTimeOffset after)
    {
        ArgumentNullException.ThrowIfNull(TimeZone);
        if (Days is { Count: 0 })
        {
            throw new InvalidOperationException("A weekly recurrence needs at least one day.");
        }

        var local = TimeZoneInfo.ConvertTime(after, TimeZone);

        // A year of candidates is enough for any day-of-week set, and bounded so a nonsensical
        // recurrence fails instead of looping.
        for (var offset = 0; offset <= 366; offset++)
        {
            var date = DateOnly.FromDateTime(local.Date).AddDays(offset);
            if (Days is not null && !Days.Contains(date.DayOfWeek))
            {
                continue;
            }

            var candidate = Resolve(date);
            if (candidate > after)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("This recurrence never comes due.");
    }

    /// <summary>Turns a local date and time of day into a real instant, DST included.</summary>
    private DateTimeOffset Resolve(DateOnly date)
    {
        var wanted = date.ToDateTime(TimeOfDay, DateTimeKind.Unspecified);

        if (TimeZone.IsInvalidTime(wanted))
        {
            // The clock sprang forward over this time: use the first moment that exists after it,
            // so the day is not silently skipped.
            var probe = wanted;
            while (TimeZone.IsInvalidTime(probe))
            {
                probe = probe.AddMinutes(1);
            }

            return new DateTimeOffset(probe, TimeZone.GetUtcOffset(probe));
        }

        if (TimeZone.IsAmbiguousTime(wanted))
        {
            // The clock fell back over this time, so it happens twice. The earlier one - the larger
            // offset - is used, and the schedule runs once.
            var offsets = TimeZone.GetAmbiguousTimeOffsets(wanted);
            return new DateTimeOffset(wanted, offsets.Max());
        }

        return new DateTimeOffset(wanted, TimeZone.GetUtcOffset(wanted));
    }
}
