using FieldKit.Modules.Configuration.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Configuration;

/// <summary>
/// Serves the custom-field catalogue to other modules. Internal — consumers bind to
/// <see cref="IFieldDefinitionCatalog"/> (AT-2).
/// </summary>
internal sealed class FieldDefinitionCatalog(ConfigurationDbContext db) : IFieldDefinitionCatalog
{
    public async Task<IReadOnlyList<FieldDefinitionDescriptor>> ForAsync(
        CustomFieldEntity entity, CancellationToken cancellationToken = default) =>
        // No tenant predicate: the global query filter supplies it. Ordered by key so a rendered form
        // and a validation error list agree on sequence without either sorting again.
        await db.FieldDefinitions
            .Where(definition => definition.Entity == entity)
            .OrderBy(definition => definition.Key)
            .Select(definition => new FieldDefinitionDescriptor(
                definition.Key,
                definition.Label,
                definition.Type,
                definition.Required,
                definition.Options,
                definition.MaxLength,
                definition.Minimum,
                definition.Maximum))
            .ToListAsync(cancellationToken);
}
