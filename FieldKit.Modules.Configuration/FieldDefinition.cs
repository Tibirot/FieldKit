using FieldKit.BuildingBlocks;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Configuration;

/// <summary>
/// One tenant-defined custom field on a core entity (<c>CFG-01</c>, ADR-0009).
/// </summary>
/// <remarks>
/// <para>
/// This is the "config-driven, fixed schema" bargain made concrete: a tenant adds a field, not a
/// table. The value lives in the owning entity's <c>CustomFields</c> JSONB and this row says what is
/// allowed there — so the schema stays fixed and migrations stay ordinary, while the data flexes.
/// </para>
/// <para>
/// <b>Current definitions only.</b> BR-CFG-1's versioning and as-of-capture validation are
/// <c>CFG-06</c>/<c>CFG-07</c>, Phase 3. Nothing captures values offline yet, so there is no window
/// in which a definition could change underneath work already done — the gap is real but not yet
/// reachable, and it arrives with the sync engine that creates it.
/// </para>
/// </remarks>
public sealed class FieldDefinition : AggregateRoot, ITenantOwned, IAuditable
{
    private readonly List<string> _options = [];

    public Guid Id { get; private set; }

    /// <summary>Which core entity carries this field.</summary>
    public CustomFieldEntity Entity { get; private set; }

    /// <summary>
    /// The property name inside the entity's <c>CustomFields</c> JSON.
    /// </summary>
    /// <remarks>
    /// Not editable after creation, and unique per entity within the tenant. Renaming it would
    /// orphan every value already stored under the old name — the rows would still be there, and
    /// nothing would read them again.
    /// </remarks>
    public string Key { get; private set; } = null!;

    /// <summary>What an admin called it. For rendering; never for matching.</summary>
    public string Label { get; private set; } = null!;

    public CustomFieldType Type { get; private set; }

    public bool Required { get; private set; }

    /// <summary>The permitted values for <see cref="CustomFieldType.Choice"/>; empty otherwise.</summary>
    public IReadOnlyList<string> Options => _options;

    /// <summary>Longest permitted text, if the tenant set one.</summary>
    public int? MaxLength { get; private set; }

    /// <summary>Smallest permitted number, if the tenant set one.</summary>
    public double? Minimum { get; private set; }

    /// <summary>Largest permitted number, if the tenant set one.</summary>
    public double? Maximum { get; private set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private FieldDefinition() { } // EF

    public static FieldDefinition Create(
        CustomFieldEntity entity,
        string key,
        string label,
        CustomFieldType type,
        bool required,
        IEnumerable<string>? options,
        int? maxLength,
        double? minimum,
        double? maximum)
    {
        var definition = new FieldDefinition { Id = Guid.CreateVersion7(), Entity = entity, Key = key };
        definition.Apply(label, type, required, options, maxLength, minimum, maximum);
        return definition;
    }

    /// <summary>
    /// Updates everything except the entity and the key — see <see cref="Key"/> for why those are fixed.
    /// </summary>
    /// <remarks>
    /// The <b>type</b> is editable, which is a deliberate risk: changing Number to Text leaves values
    /// already stored that no longer match their definition, and nothing rewrites them. Refusing the
    /// change would be safer and would also mean a tenant who picked the wrong type once can never
    /// correct it without losing the field. Validation runs on write, so the mismatch surfaces the
    /// next time each entity is saved rather than silently — which is the trade being made.
    /// </remarks>
    public void Update(
        string label,
        CustomFieldType type,
        bool required,
        IEnumerable<string>? options,
        int? maxLength,
        double? minimum,
        double? maximum,
        IClock clock)
    {
        Apply(label, type, required, options, maxLength, minimum, maximum);
        ModifiedAtUtc = clock.UtcNow;
    }

    public FieldDefinitionDescriptor ToDescriptor() =>
        new(Key, Label, Type, Required, Options, MaxLength, Minimum, Maximum);

    private void Apply(
        string label,
        CustomFieldType type,
        bool required,
        IEnumerable<string>? options,
        int? maxLength,
        double? minimum,
        double? maximum)
    {
        Label = label;
        Type = type;
        Required = required;
        MaxLength = maxLength;
        Minimum = minimum;
        Maximum = maximum;

        _options.Clear();

        // Options only mean something for a choice. Keeping them on other types would leave a list
        // that renders nowhere and validates nothing — and would survive a type change to become
        // quietly authoritative again.
        if (type == CustomFieldType.Choice && options is not null)
        {
            _options.AddRange(options.Where(option => !string.IsNullOrWhiteSpace(option)).Distinct(StringComparer.Ordinal));
        }
    }
}
