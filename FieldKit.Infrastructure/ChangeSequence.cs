using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldKit.Infrastructure;

/// <summary>
/// One row per tenant, holding the last row version this module handed out (ADR-0013).
/// </summary>
/// <remarks>
/// Every module schema owns one, the same way every module schema owns an outbox table — a shared
/// mechanism, a private table, no cross-schema reference (ADR-0005, ADR-0006).
/// </remarks>
public sealed class ChangeSequence
{
    public TenantId TenantId { get; set; }

    /// <summary>
    /// The last version issued. Also the row's concurrency token, which is the entire design: two
    /// transactions racing for the next number cannot both win, so no two change sets share a
    /// version and none is skipped by a rollback.
    /// </summary>
    public long Value { get; set; }
}

internal sealed class ChangeSequenceConfiguration : IEntityTypeConfiguration<ChangeSequence>
{
    public void Configure(EntityTypeBuilder<ChangeSequence> builder)
    {
        builder.ToTable("change_sequence");
        builder.HasKey(sequence => sequence.TenantId);

        // The token that serializes allocation. EF adds `WHERE value = @original` to the UPDATE and
        // raises DbUpdateConcurrencyException when it matches nothing — which is how the loser of a
        // race is told to retry rather than quietly reusing a number.
        builder.Property(sequence => sequence.Value)
            .IsConcurrencyToken()
            .IsRequired();
    }
}
