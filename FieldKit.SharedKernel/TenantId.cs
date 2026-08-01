namespace FieldKit.SharedKernel;

/// <summary>
/// Strongly-typed tenant identifier. Every tenant-owned entity carries one; it is resolved from the
/// auth token into the ambient tenant context and enforced by the global query filter (ADR-0008).
/// </summary>
public readonly record struct TenantId(Guid Value)
{
    /// <summary>A new sequential (v7) id — index-friendly and client-generatable offline.</summary>
    public static TenantId New() => new(Guid.CreateVersion7());

    public static TenantId Parse(string value) => new(Guid.Parse(value));

    public override string ToString() => Value.ToString();
}
