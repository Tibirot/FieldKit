using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Journey;

/// <summary>
/// A date nobody works — a public holiday, or a day the business closes (<c>JRN-02</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant-wide, not per rep.</b> This is the exception to the working pattern that everybody
/// shares; a particular rep being away is a different thing and is not modelled here. That is a real
/// gap and it is deliberate: leave is either an absence the business already tracks somewhere else,
/// or it is <c>JRN-08</c>'s rescheduling — and a half-built leave calendar that a supervisor half
/// trusts is worse than none. If it lands, it lands as its own entity rather than by making this one
/// nullable, because "a day nobody works" and "a day this person does not" resolve differently and a
/// nullable owner would make one query answer two questions.
/// </para>
/// <para>
/// <b>Dated, not recurring.</b> Easter moves, and so do the substitute days a government grants when
/// a fixed holiday falls on a weekend — a recurrence rule would be wrong every few years in a way
/// nobody notices until a plan sends a rep out on a closed day. A tenant enters the year's dates,
/// which is the same thing every payroll system asks of them.
/// </para>
/// <para>
/// <b>Stored as a date, in no timezone.</b> A holiday is a statement about a day, the same reasoning
/// <c>RepAssignment</c>'s period carries: a timestamp would invite a conversion that moves the
/// boundary by a few hours, and "is the 25th a holiday" has one answer.
/// </para>
/// </remarks>
public sealed class Holiday : AggregateRoot, ITenantOwned, IAuditable
{
    /// <summary>The column width. Long enough for "Prima zi de Rusalii (substitute day)".</summary>
    public const int MaximumNameLength = 100;

    public Guid Id { get; private set; }

    /// <summary>The day itself. Unique within the tenant.</summary>
    public DateOnly Date { get; private set; }

    /// <summary>
    /// What it is called.
    /// </summary>
    /// <remarks>
    /// Required, because a plan that skips a day should be able to say why. "Not planned" with no
    /// reason is the kind of gap a supervisor files a bug about; "Christmas Day" is not.
    /// </remarks>
    public string Name { get; private set; } = null!;

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private Holiday() { } // EF

    public static Holiday Create(DateOnly date, string name) =>
        new() { Id = Guid.CreateVersion7(), Date = date, Name = name.Trim() };

    public void Rename(string name, IClock clock)
    {
        Name = name.Trim();
        ModifiedAtUtc = clock.UtcNow;
    }
}
