using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Journey;

/// <summary>One day a rep can be sent out, and how many calls it holds.</summary>
public sealed record WorkingDay(DateOnly Date, int Capacity);

/// <summary>
/// The rep's working days in a period, with the holidays taken out (<c>JRN-02</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Internal, like <c>FrequencyResolver</c>.</b> Its only caller is generation (<c>JRN-03</c>),
/// which lives in this module, so there is nothing to expose and no contract to guess at.
/// </para>
/// <para>
/// <b>A materialised list of dates rather than a predicate.</b> Generation has to *distribute* a
/// number of visits across the days available — it needs to count them, index them and spread work
/// over them — so an "is this day workable?" callback would make the caller write the loop that
/// produces this list anyway, once per generator.
/// </para>
/// <para>
/// <b>Empty when the rep has no calendar</b>, which is the same "unconfigured" answer
/// <c>FrequencyResolver</c> gives for an outlet nobody has graded, and for the same reason: the
/// alternative is planning against an assumed Monday-to-Friday that nobody chose, which is a plan
/// that looks configured and is a guess.
/// </para>
/// </remarks>
internal sealed class CalendarReader(JourneyDbContext db)
{
    /// <summary>
    /// The longest period this will answer for.
    /// </summary>
    /// <remarks>
    /// A cycle is at most a year (<see cref="CallFrequency.MaximumCycleLengthDays"/>) and generation
    /// runs a cycle at a time, so a year and a bit is the widest honest ask. The bound exists because
    /// this walks day by day: an unbounded range is an unbounded loop reachable from an endpoint.
    /// </remarks>
    public const int MaximumSpanDays = 400;

    public async Task<IReadOnlyList<WorkingDay>> ForRepAsync(
        string userId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || to < from) return [];
        if (to.DayNumber - from.DayNumber + 1 > MaximumSpanDays) return [];

        var calendar = await db.WorkingCalendars
            .SingleOrDefaultAsync(row => row.UserId == userId, cancellationToken);

        if (calendar is null) return [];

        // Only the holidays in range. A tenant enters a year at a time and generation asks for a
        // cycle, so loading the lot would read most of a table to use a fortnight of it.
        var holidays = await db.Holidays
            .Where(holiday => holiday.Date >= from && holiday.Date <= to)
            .Select(holiday => holiday.Date)
            .ToListAsync(cancellationToken);

        var closed = holidays.ToHashSet();
        var days = new List<WorkingDay>();

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            // A holiday removes the day rather than zeroing its capacity. A day with no capacity and
            // a day that is not worked are the same to a generator, and keeping both shapes would
            // mean every caller had to remember to filter — see WorkingDay, which has no "is this a
            // real day" flag precisely so it cannot be ignored.
            if (calendar.Works(date) && !closed.Contains(date))
            {
                days.Add(new WorkingDay(date, calendar.VisitsPerDay));
            }
        }

        return days;
    }
}
