namespace FieldKit.SharedKernel;

/// <summary>
/// A span of business days — inclusive at both ends, and open-ended when <see cref="To"/> is null.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dates, not instants.</b> "This rep covers this territory from 1 March" is a statement about
/// days, not about a moment; storing it as a timestamp would invite a timezone conversion that
/// silently moves the boundary by a few hours. Which timezone decides what "today" is belongs to the
/// caller asking the question, not to the range itself — see <see cref="Contains"/>.
/// </para>
/// <para>
/// <b>Inclusive at both ends</b>, because that is how people write and read them: an assignment
/// "1–31 March" covers 31 March. The half-open convention is easier to compose and reads wrong on a
/// screen; this type is used where a human typed the dates.
/// </para>
/// </remarks>
public readonly record struct DateRange
{
    public DateOnly From { get; }

    /// <summary>The last day, or null for "until further notice".</summary>
    public DateOnly? To { get; }

    public DateRange(DateOnly from, DateOnly? to)
    {
        if (to is { } end && end < from)
        {
            throw new ArgumentOutOfRangeException(nameof(to), to, "A range cannot end before it starts.");
        }

        From = from;
        To = to;
    }

    /// <summary>Creates a range, or reports that it is not one. For untrusted input — see <see cref="GeoPoint.TryCreate"/>.</summary>
    public static bool TryCreate(DateOnly from, DateOnly? to, out DateRange range)
    {
        if (to is { } end && end < from)
        {
            range = default;
            return false;
        }

        range = new DateRange(from, to);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="day"/> falls inside this range.
    /// </summary>
    /// <remarks>
    /// Takes the day rather than reading a clock, so the caller decides whose "today" this is. That
    /// is the whole reason this type has no <c>IsCurrent</c>: the answer depends on a timezone the
    /// range cannot know, and a convenience method would have quietly picked one.
    /// </remarks>
    public bool Contains(DateOnly day) => day >= From && (To is null || day <= To);

    /// <summary>
    /// Whether two ranges share at least one day.
    /// </summary>
    /// <remarks>
    /// The rule BR-ORG-2 rests on. Open ends are treated as infinity in the obvious direction, which
    /// is why the null checks come first: <c>a.From &lt;= b.To</c> is not a comparison that can be
    /// written against a null, and getting that wrong is how "no overlap" ends up meaning "no overlap
    /// between two closed ranges".
    /// </remarks>
    public bool Overlaps(DateRange other) =>
        (other.To is null || From <= other.To) && (To is null || other.From <= To);

    public override string ToString() => $"{From:O} – {(To is { } to ? to.ToString("O") : "…")}";
}
