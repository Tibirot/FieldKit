using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Journey;

/// <summary>
/// One outlet's call frequency, overriding whatever its segment says (<c>JRN-01</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The exception, and it exists because the default is a generalisation.</b> A segment says how
/// often shops of that grade are worth visiting; a particular shop is the chain's flagship, or is
/// three hours away, or has just been put on a recovery plan. Without a per-outlet answer the only
/// way to say that is to invent a segment for one shop, which corrupts the vocabulary every report
/// groups by.
/// </para>
/// <para>
/// <b>Specificity, the same ladder pricing uses.</b> <c>BR-PRD-2</c> resolves a price outlet →
/// channel → default, and this resolves a frequency outlet → segment. Deliberately the same shape,
/// because it is the same question — the most specific rule that names this shop wins — and a system
/// where two configuration ladders resolve differently is one where an admin has to remember which
/// screen they are on.
/// </para>
/// <para>
/// <b>The outlet id is not a foreign key</b>, because Outlets is another module and its table is
/// behind a schema boundary (ADR-0005 / AT-1). It is validated on the way in through
/// <c>IOutletCatalog</c> instead, so a rule cannot name a shop this tenant does not have — and if an
/// outlet is later deleted, the orphan rule resolves against nothing rather than breaking a join.
/// </para>
/// </remarks>
public sealed class OutletFrequency : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }

    /// <summary>The outlet this is about. Unique within the tenant — one shop, one override.</summary>
    public Guid OutletId { get; private set; }

    public int VisitsPerCycle { get; private set; }

    public int CycleLengthDays { get; private set; }

    /// <summary>The pair as a value — see <see cref="CallFrequency"/> for why they travel together.</summary>
    public CallFrequency Frequency =>
        CallFrequency.TryCreate(VisitsPerCycle, CycleLengthDays, out var frequency)
            ? frequency
            : throw new InvalidOperationException(
                $"Outlet {OutletId} holds {VisitsPerCycle} visits over {CycleLengthDays} days, which is not a frequency.");

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private OutletFrequency() { } // EF

    public static OutletFrequency Create(Guid outletId, CallFrequency frequency) => new()
    {
        Id = Guid.CreateVersion7(),
        OutletId = outletId,
        VisitsPerCycle = frequency.VisitsPerCycle,
        CycleLengthDays = frequency.CycleLengthDays,
    };

    public void Set(CallFrequency frequency, IClock clock)
    {
        VisitsPerCycle = frequency.VisitsPerCycle;
        CycleLengthDays = frequency.CycleLengthDays;
        ModifiedAtUtc = clock.UtcNow;
    }
}
