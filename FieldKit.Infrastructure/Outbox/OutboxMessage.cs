using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FieldKit.Infrastructure.Outbox;

/// <summary>
/// A pending integration event, written to a module's own <c>outbox_message</c> table in the same
/// transaction as the domain change (ADR-0006). Its <see cref="Id"/> is the event id — the natural
/// idempotency key. Each module owns its outbox table (schema-per-module).
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; }

    /// <summary>The event's assembly-qualified .NET type name, used to rehydrate it.</summary>
    public required string Type { get; init; }

    /// <summary>The serialized event (JSON / jsonb).</summary>
    public required string Content { get; init; }

    public DateTimeOffset OccurredOnUtc { get; init; }
    public DateTimeOffset? ProcessedOnUtc { get; set; }
    public string? Error { get; set; }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_message");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Type).HasMaxLength(500).IsRequired();
        builder.Property(m => m.Content).HasColumnType("jsonb").IsRequired();
        // Unprocessed rows are the hot query; index them.
        builder.HasIndex(m => m.ProcessedOnUtc);
    }
}
