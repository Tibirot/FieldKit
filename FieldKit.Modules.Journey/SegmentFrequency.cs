using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Journey;

/// <summary>
/// The call frequency every outlet in a segment gets unless one of them says otherwise
/// (<c>JRN-01</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The default, and the reason there is a default at all.</b> A tenant with four thousand shops
/// segments them precisely so it can say "A-grade shops are visited weekly" once instead of four
/// thousand times, and the decisions ledger settles this as <i>segment default, overridable</i>
/// ([B3] / the spec's open question). So the segment rule is the one an admin writes and the outlet
/// rule is the exception.
/// </para>
/// <para>
/// <b>Keyed by the segment string, not by an id, because a segment is not an entity here.</b>
/// Outlets stores it as free text on the shop (<c>OUT-01</c>) — there is no segment table to point
/// at — so this keys on the label and compares it exactly as the tenant typed it. That is a real
/// sharp edge: "A" and "a" are two segments, and a tenant who types both gets two rules and half
/// their shops on each. It is refused at the door instead — see <c>Normalise</c>.
/// </para>
/// <para>
/// Not an aggregate with much to it: the rule is the row, and editing one is replacing the numbers.
/// The interesting behaviour lives in <see cref="CallFrequency"/> and in resolution.
/// </para>
/// </remarks>
public sealed class SegmentFrequency : AggregateRoot, ITenantOwned, IAuditable
{
    /// <summary>The column width, which is also Outlets' width for the same string.</summary>
    public const int MaximumSegmentLength = 50;

    public Guid Id { get; private set; }

    /// <summary>The segment label, as stored. Unique within the tenant.</summary>
    public string Segment { get; private set; } = null!;

    public int VisitsPerCycle { get; private set; }

    public int CycleLengthDays { get; private set; }

    /// <summary>The pair as a value — see <see cref="CallFrequency"/> for why they travel together.</summary>
    public CallFrequency Frequency =>
        CallFrequency.TryCreate(VisitsPerCycle, CycleLengthDays, out var frequency)
            ? frequency
            // Unreachable through the factory and the check constraints, and thrown rather than
            // defaulted: a row that is not a frequency is a corrupted rule, and silently reading it
            // as 0×/0d would plan nothing for a whole segment without saying so.
            : throw new InvalidOperationException(
                $"Segment '{Segment}' holds {VisitsPerCycle} visits over {CycleLengthDays} days, which is not a frequency.");

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private SegmentFrequency() { } // EF

    public static SegmentFrequency Create(string segment, CallFrequency frequency) => new()
    {
        Id = Guid.CreateVersion7(),
        Segment = Normalise(segment),
        VisitsPerCycle = frequency.VisitsPerCycle,
        CycleLengthDays = frequency.CycleLengthDays,
    };

    public void Set(CallFrequency frequency, IClock clock)
    {
        VisitsPerCycle = frequency.VisitsPerCycle;
        CycleLengthDays = frequency.CycleLengthDays;
        ModifiedAtUtc = clock.UtcNow;
    }

    /// <summary>
    /// Trimmed. Deliberately <b>not</b> case-folded.
    /// </summary>
    /// <remarks>
    /// Trimming catches the copy-paste with a trailing space, which is invisible on screen and would
    /// otherwise be a second segment nobody can tell from the first. Case is left alone because the
    /// segment on the outlet is left alone: upper-casing here would make this rule stop matching the
    /// shops it is about. Matching is case-insensitive instead, which is a decision about the
    /// comparison rather than about the tenant's data — see <c>FrequencyResolver</c>.
    /// </remarks>
    public static string Normalise(string segment) => segment.Trim();
}
