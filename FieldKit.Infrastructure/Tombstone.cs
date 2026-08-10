using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldKit.Infrastructure;

/// <summary>
/// The record that a row a device may be holding no longer exists (ADR-0007, W8 slice 1).
/// </summary>
/// <remarks>
/// <para>
/// A delta pull asks "what changed after version N". A deleted row cannot answer, because it is
/// gone — so without this a device adds and updates forever and never removes, and keeps a channel
/// that was deleted months ago. The row is deleted; the *fact of the deletion* is what survives, and
/// it carries a row version so it sorts into the same stream as the changes around it.
/// </para>
/// <para>
/// Keyed by entity type and id rather than by a surrogate, so deleting an id that was previously
/// deleted and recreated updates the existing tombstone to the newer version instead of leaving two
/// rows disagreeing about when it died.
/// </para>
/// </remarks>
public sealed class Tombstone
{
    public TenantId TenantId { get; set; }

    /// <summary>The CLR type name of what was deleted, e.g. <c>Channel</c>.</summary>
    public string EntityType { get; set; } = null!;

    public Guid EntityId { get; set; }

    /// <summary>Allocated by the same counter as live changes, so a delta orders them together.</summary>
    public long RowVersion { get; set; }

    public DateTimeOffset DeletedAtUtc { get; set; }
}

internal sealed class TombstoneConfiguration : IEntityTypeConfiguration<Tombstone>
{
    public void Configure(EntityTypeBuilder<Tombstone> builder)
    {
        builder.ToTable("tombstone");
        builder.HasKey(tombstone => new { tombstone.TenantId, tombstone.EntityType, tombstone.EntityId });

        builder.Property(tombstone => tombstone.EntityType).HasMaxLength(128).IsRequired();

        // The access path of every delta pull: "tombstones for this tenant after version N".
        builder.HasIndex(tombstone => new { tombstone.TenantId, tombstone.RowVersion });
    }
}
