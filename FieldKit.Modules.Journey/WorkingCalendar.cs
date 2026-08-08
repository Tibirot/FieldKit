using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Journey;

/// <summary>
/// One rep's working pattern: which days of the week they work, and how many calls fit in a day
/// (<c>JRN-02</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Per rep, and there is deliberately no tenant-wide default.</b> Frequency has one — a segment
/// rule that many shops inherit — because a segment is a *classification* several outlets genuinely
/// share. A calendar default would key on nothing but "this tenant", which is a fallback rather than
/// a classification, and inventing one before anybody asks is the guess this codebase keeps
/// refusing to make (see the module registry on `IRepScope`). A rep with no calendar is
/// <i>unconfigured</i>, exactly as an outlet with no frequency is, and generation says so rather
/// than planning against an assumed Monday-to-Friday.
/// </para>
/// <para>
/// <b>Days of the week, not dates.</b> A pattern repeats; a calendar of dates would have to be
/// extended forever and would go stale the moment planning ran past the end of it. Dates enter only
/// as <see cref="Holiday"/> — the exceptions to the pattern.
/// </para>
/// <para>
/// <b>Hours are not modelled</b>, though the spec's phrase is "working days/hours". Capacity is
/// visits per day, and that is what <c>BR-JRN-3</c> is written in terms of: the generator packs a
/// day by *count*, not by clock time. Hours would only matter to a generator that scheduled
/// appointments, which is the day-sequencing heuristic deferred to <c>JRN-09</c> in Phase 3 — and
/// modelling them now would be a column nothing reads.
/// </para>
/// </remarks>
public sealed class WorkingCalendar : AggregateRoot, ITenantOwned, IAuditable
{
    /// <summary>
    /// The most calls a day can hold.
    /// </summary>
    /// <remarks>
    /// A sanity bound, not a business rule — nobody makes sixty calls in a day, and a capacity that
    /// large is a typo that would let generation pack a whole cycle into one day while looking
    /// configured. The real limit is the rep's, and it is whatever they set below this.
    /// </remarks>
    public const int MaximumVisitsPerDay = 50;

    private readonly List<DayOfWeek> _workingDays = [];

    public Guid Id { get; private set; }

    /// <summary>The Keycloak subject — the same identifier assignments and positions use.</summary>
    public string UserId { get; private set; } = null!;

    /// <summary>Which days they work. Never empty — see <see cref="TryCreate"/>.</summary>
    public IReadOnlyList<DayOfWeek> WorkingDays => _workingDays;

    /// <summary>How many calls fit in one of those days (<c>BR-JRN-3</c>).</summary>
    public int VisitsPerDay { get; private set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private WorkingCalendar() { } // EF

    /// <summary>
    /// Builds one, or refuses a pattern that could not be worked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A rep who works no days is refused rather than stored.</b> It is the same shape as a zero
    /// call frequency: a calendar with no working days and no calendar at all produce the same empty
    /// plan, and the second already says it. Someone on long-term leave is a
    /// <i>deactivated user</i> (<c>IAM-03</c>) or an unassigned territory, both of which say so where
    /// the rest of the system can see it.
    /// </para>
    /// <para>
    /// Duplicates are collapsed rather than refused — "Monday, Monday, Wednesday" is a caller being
    /// careless with a list, not an admin describing something impossible, and it means one thing.
    /// The result is sorted so two calendars holding the same days compare and read the same.
    /// </para>
    /// </remarks>
    public static bool TryCreate(
        string userId, IEnumerable<DayOfWeek> workingDays, int visitsPerDay, out WorkingCalendar calendar)
    {
        calendar = null!;

        if (string.IsNullOrWhiteSpace(userId)) return false;
        if (visitsPerDay is < 1 or > MaximumVisitsPerDay) return false;

        var days = Normalise(workingDays);
        if (days.Count == 0) return false;

        calendar = new WorkingCalendar { Id = Guid.CreateVersion7(), UserId = userId, VisitsPerDay = visitsPerDay };
        calendar._workingDays.AddRange(days);

        return true;
    }

    /// <summary>Replaces the pattern wholesale. Returns false for one that could not be worked.</summary>
    public bool TrySet(IEnumerable<DayOfWeek> workingDays, int visitsPerDay, IClock clock)
    {
        if (visitsPerDay is < 1 or > MaximumVisitsPerDay) return false;

        var days = Normalise(workingDays);
        if (days.Count == 0) return false;

        _workingDays.Clear();
        _workingDays.AddRange(days);
        VisitsPerDay = visitsPerDay;
        ModifiedAtUtc = clock.UtcNow;

        return true;
    }

    /// <summary>Whether this rep works on <paramref name="day"/>, ignoring holidays.</summary>
    /// <remarks>
    /// Holidays are the other half of the answer and are not this type's to know — they belong to the
    /// tenant, not to one rep's pattern. <c>CalendarReader</c> is what puts the two together.
    /// </remarks>
    public bool Works(DateOnly day) => _workingDays.Contains(day.DayOfWeek);

    private static List<DayOfWeek> Normalise(IEnumerable<DayOfWeek> workingDays) =>
        [.. workingDays.Where(Enum.IsDefined).Distinct().Order()];
}
