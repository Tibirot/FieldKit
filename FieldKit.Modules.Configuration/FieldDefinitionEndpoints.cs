using System.Text.Json.Serialization;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Configuration;

/// <summary>
/// A custom field an admin has defined.
/// </summary>
/// <remarks>
/// The enums travel as their names, per-property, for the reason the outlet contract gives: an
/// ordinal on the wire makes the API's meaning depend on the order members happen to sit in an enum.
/// It matters more here than anywhere else — these
/// enums are a <i>contract</i> other modules share, so a member inserted in the middle would silently
/// re-point every stored definition rather than breaking a build.
/// </remarks>
public sealed record FieldDefinitionResponse(
    Guid Id,
    [property: JsonConverter(typeof(JsonStringEnumConverter<CustomFieldEntity>))] CustomFieldEntity Entity,
    string Key,
    string Label,
    [property: JsonConverter(typeof(JsonStringEnumConverter<CustomFieldType>))] CustomFieldType Type,
    bool Required,
    IReadOnlyList<string> Options,
    int? MaxLength,
    double? Minimum,
    double? Maximum);

/// <summary>Define a custom field. The entity and key are fixed after creation.</summary>
public sealed record CreateFieldDefinitionRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter<CustomFieldEntity>))] CustomFieldEntity Entity,
    string Key,
    string Label,
    [property: JsonConverter(typeof(JsonStringEnumConverter<CustomFieldType>))] CustomFieldType Type,
    bool Required = false,
    IReadOnlyList<string>? Options = null,
    int? MaxLength = null,
    double? Minimum = null,
    double? Maximum = null);

/// <summary>Update a custom field. No entity or key — see <see cref="FieldDefinition.Key"/>.</summary>
public sealed record UpdateFieldDefinitionRequest(
    string Label,
    [property: JsonConverter(typeof(JsonStringEnumConverter<CustomFieldType>))] CustomFieldType Type,
    bool Required = false,
    IReadOnlyList<string>? Options = null,
    int? MaxLength = null,
    double? Minimum = null,
    double? Maximum = null);

/// <summary>
/// The custom-field catalogue (<c>CFG-01</c>).
/// </summary>
internal static class FieldDefinitionEndpoints
{
    /// <summary>
    /// Keys go into JSON and into future index expressions, so they are identifiers rather than prose.
    /// </summary>
    private const string KeyPattern = "^[a-z][a-z0-9_]{0,59}$";

    public static void MapFieldDefinitionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var definitions = endpoints.MapGroup("/api/config/field-definitions").WithTags("Configuration");

        definitions.MapGet("/", async (
                CustomFieldEntity? entity, ConfigurationDbContext db, CancellationToken ct) =>
            await db.FieldDefinitions
                .Where(definition => entity == null || definition.Entity == entity)
                .OrderBy(definition => definition.Entity).ThenBy(definition => definition.Key)
                .Select(definition => new FieldDefinitionResponse(
                    definition.Id,
                    definition.Entity,
                    definition.Key,
                    definition.Label,
                    definition.Type,
                    definition.Required,
                    definition.Options,
                    definition.MaxLength,
                    definition.Minimum,
                    definition.Maximum))
                .ToListAsync(ct))
            .RequirePermission(ConfigurationPermissions.Read);

        definitions.MapPost("/", async (
            CreateFieldDefinitionRequest request, ConfigurationDbContext db, CancellationToken ct) =>
        {
            var problem = Validate(
                request.Entity, request.Key, request.Label, request.Type,
                request.Options, request.MaxLength, request.Minimum, request.Maximum);

            if (problem is not null) return problem;

            var taken = await db.FieldDefinitions.AnyAsync(
                definition => definition.Entity == request.Entity && definition.Key == request.Key, ct);

            if (taken)
            {
                return Results.Conflict(new
                {
                    error = $"'{request.Key}' is already defined for {request.Entity}.",
                });
            }

            var created = FieldDefinition.Create(
                request.Entity, request.Key, request.Label, request.Type, request.Required,
                request.Options, request.MaxLength, request.Minimum, request.Maximum);

            db.FieldDefinitions.Add(created);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/config/field-definitions/{created.Id}", ToResponse(created));
        }).RequirePermission(ConfigurationPermissions.Write);

        definitions.MapPut("/{id:guid}", async (
            Guid id, UpdateFieldDefinitionRequest request, ConfigurationDbContext db,
            IClock clock, CancellationToken ct) =>
        {
            var definition = await db.FieldDefinitions.SingleOrDefaultAsync(d => d.Id == id, ct);
            if (definition is null) return Results.NotFound();

            var problem = Validate(
                definition.Entity, definition.Key, request.Label, request.Type,
                request.Options, request.MaxLength, request.Minimum, request.Maximum);

            if (problem is not null) return problem;

            definition.Update(
                request.Label, request.Type, request.Required, request.Options,
                request.MaxLength, request.Minimum, request.Maximum, clock);

            await db.SaveChangesAsync(ct);

            return Results.Ok(ToResponse(definition));
        }).RequirePermission(ConfigurationPermissions.Write);

        definitions.MapDelete("/{id:guid}", async (
            Guid id, ConfigurationDbContext db, CancellationToken ct) =>
        {
            var definition = await db.FieldDefinitions.SingleOrDefaultAsync(d => d.Id == id, ct);
            if (definition is null) return Results.NotFound();

            // Deleted outright, and the values already stored under this key are left where they are.
            //
            // Configuration cannot reach into another module's tables to clean them (ADR-0005), and
            // should not: the owning module decides what an unknown key means to it. Today Outlets
            // rejects unknown keys on the *next* write, so a deleted field's values persist until the
            // outlet is next saved and then disappear. That is worth knowing before deleting one.
            db.FieldDefinitions.Remove(definition);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(ConfigurationPermissions.Write);
    }

    /// <summary>
    /// Rejects a definition that could not describe a usable field.
    /// </summary>
    /// <remarks>
    /// The rules are about the <i>definition</i>, not about values: a choice with no options can
    /// never be satisfied, and a minimum above its maximum admits nothing. Both would save happily
    /// and then reject every value an admin tried, with the failure appearing to be the value's.
    /// </remarks>
    private static IResult? Validate(
        CustomFieldEntity entity,
        string key,
        string label,
        CustomFieldType type,
        IReadOnlyList<string>? options,
        int? maxLength,
        double? minimum,
        double? maximum)
    {
        var errors = new List<string>();

        if (!Enum.IsDefined(entity)) errors.Add("Unknown entity.");
        if (!Enum.IsDefined(type)) errors.Add("Unknown field type.");

        if (!System.Text.RegularExpressions.Regex.IsMatch(key ?? "", KeyPattern))
        {
            errors.Add("A key must be lowercase letters, digits and underscores, starting with a letter.");
        }

        if (string.IsNullOrWhiteSpace(label)) errors.Add("A field needs a label.");

        if (type == CustomFieldType.Choice && (options is null || options.Count == 0))
        {
            errors.Add("A choice field needs at least one option.");
        }

        if (maxLength is <= 0) errors.Add("MaxLength must be positive.");

        if (minimum is { } min && maximum is { } max && min > max)
        {
            errors.Add("Minimum cannot be greater than maximum.");
        }

        return errors.Count == 0 ? null : Results.BadRequest(new { errors });
    }

    private static FieldDefinitionResponse ToResponse(FieldDefinition definition) =>
        new(definition.Id, definition.Entity, definition.Key, definition.Label, definition.Type,
            definition.Required, definition.Options, definition.MaxLength,
            definition.Minimum, definition.Maximum);
}
