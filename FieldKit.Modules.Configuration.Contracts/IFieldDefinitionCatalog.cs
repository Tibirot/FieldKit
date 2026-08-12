using System.Text.Json.Serialization;

namespace FieldKit.Modules.Configuration.Contracts;

/// <summary>
/// The core entities that can carry tenant custom fields (ADR-0009).
/// </summary>
/// <remarks>
/// A closed set, deliberately. ADR-0009's "explicitly out" list starts with tenant-defined
/// <i>entities</i>: the schema is fixed and only the fields flex. An enum says that in the type
/// system — a string would leave "which entities exist" as a question the catalogue could not answer
/// and a typo could extend.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<CustomFieldEntity>))]
public enum CustomFieldEntity
{
    Outlet = 0,
    Product = 1,
    Order = 2,
    Visit = 3,
}

/// <summary>
/// What kind of value a custom field holds.
/// </summary>
/// <remarks>
/// Five types, each with an unambiguous JSON representation and a validation rule that can be
/// stated in one sentence. Richer kinds — multi-choice, file, formula — wait for something that
/// needs them: a type nobody uses still has to be validated, synced, rendered and migrated.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<CustomFieldType>))]
public enum CustomFieldType
{
    Text = 0,
    Number = 1,
    Boolean = 2,
    Date = 3,

    /// <summary>One value from <see cref="FieldDefinitionDescriptor.Options"/>.</summary>
    Choice = 4,
}

/// <summary>
/// One custom field a tenant has defined, as other modules see it.
/// </summary>
/// <param name="Key">The property name inside the entity's <c>CustomFields</c> JSON.</param>
/// <param name="Label">What an admin called it — for rendering, never for matching.</param>
/// <param name="Required">Whether a value must be present when the entity is saved.</param>
/// <param name="Options">The permitted values for <see cref="CustomFieldType.Choice"/>; empty otherwise.</param>
/// <param name="MaxLength">Longest permitted text, if the tenant set one.</param>
/// <param name="Minimum">Smallest permitted number, if the tenant set one.</param>
/// <param name="Maximum">Largest permitted number, if the tenant set one.</param>
public sealed record FieldDefinitionDescriptor(
    string Key,
    string Label,
    CustomFieldType Type,
    bool Required,
    IReadOnlyList<string> Options,
    int? MaxLength,
    double? Minimum,
    double? Maximum);

/// <summary>
/// The per-tenant custom-field catalogue (<c>CFG-01</c>, ADR-0009).
/// </summary>
/// <remarks>
/// <para>
/// Consumed by every module whose entities carry custom fields. The owning module validates its own
/// values against these definitions (<c>CFG-02</c>) rather than handing them here: the rule belongs
/// where the data is written, and each module can then fail with a message about its own entity
/// instead of a generic one.
/// </para>
/// <para>
/// Definitions are <b>current only</b>. BR-CFG-1's versioning and as-of-capture validation are
/// <c>CFG-06</c>/<c>CFG-07</c>, Phase 3, and arrive with the sync engine that needs them — nothing
/// captures values offline yet, so there is no window in which a definition could change underneath
/// captured work.
/// </para>
/// <para>
/// Scoped to the current tenant by the global query filter. There is no tenant parameter, because a
/// caller able to pass one is a caller able to pass the wrong one.
/// </para>
/// </remarks>
public interface IFieldDefinitionCatalog
{
    /// <summary>Every custom field defined for one entity, ordered by key.</summary>
    Task<IReadOnlyList<FieldDefinitionDescriptor>> ForAsync(
        CustomFieldEntity entity, CancellationToken cancellationToken = default);
}
