using FieldKit.Modules.Configuration.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Configuration;

/// <summary>
/// Answers <see cref="ISurveyForms"/> from the configured forms (<c>AUD-04</c>, <c>CFG-04</c>).
/// </summary>
/// <remarks>
/// <para>
/// Both reads <c>Include</c> the questions, because a form without them is not an answer to anything
/// a caller asked: an audit resolving a form is about to render it, and a screen listing forms shows
/// how many questions each asks. A lazy navigation here would turn one query into one per form.
/// </para>
/// <para>
/// It reads only Configuration's own schema (AT-1).
/// </para>
/// </remarks>
internal sealed class SurveyFormCatalog(ConfigurationDbContext db) : ISurveyForms
{
    public async Task<SurveyFormDescriptor?> ByIdAsync(
        Guid formId, CancellationToken cancellationToken = default)
    {
        var form = await db.SurveyForms
            .Include(row => row.Questions)
            .SingleOrDefaultAsync(row => row.Id == formId, cancellationToken);

        return form?.Describe();
    }

    public async Task<IReadOnlyList<SurveyFormDescriptor>> AllAsync(
        CancellationToken cancellationToken = default)
    {
        // By name, because that is what an admin picks one by and the only ordering they can predict.
        var forms = await db.SurveyForms
            .Include(row => row.Questions)
            .OrderBy(row => row.Name)
            .ToListAsync(cancellationToken);

        return [.. forms.Select(form => form.Describe())];
    }
}
