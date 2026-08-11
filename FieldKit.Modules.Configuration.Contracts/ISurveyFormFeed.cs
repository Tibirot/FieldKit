using FieldKit.SharedKernel;

namespace FieldKit.Modules.Configuration.Contracts;

/// <summary>One question, as it crosses the wire to a device.</summary>
/// <remarks>
/// Deliberately not <see cref="SurveyQuestionDescriptor"/>, which carries the
/// <see cref="SurveyQuestionType"/> enum. Serialised, an enum is an <i>ordinal</i>: inserting a value
/// in the middle of that list would silently reinterpret every form already stored on every device.
/// The name is the stable thing, so the name is what travels — the same call
/// <see cref="VisitWorkflowStepSnapshot"/> makes.
/// </remarks>
public sealed record SurveyQuestionSnapshot(
    int Order,
    string Key,
    string Text,
    string Type,
    bool Mandatory,
    IReadOnlyList<string> Options);

/// <summary>
/// One survey form as the device holds it (<c>AUD-04</c>, <c>CFG-04</c>, sync engine §3).
/// </summary>
/// <remarks>
/// <b>The questions travel inside it</b>, for the reason a workflow's steps do: a form is only ever
/// useful whole. A device holding four of five questions would ask a rep less than the tenant
/// configured, and <c>BR-AUD-7</c> would gate the audit step on a mandatory question it never
/// received. Sending the aggregate as one row makes a partial form unrepresentable.
/// </remarks>
public sealed record SurveyFormSnapshot(
    Guid Id, string Name, IReadOnlyList<SurveyQuestionSnapshot> Questions, long RowVersion);

/// <summary>One page of survey-form changes: what to upsert, what to drop, and how far the device is.</summary>
public sealed record SurveyFormChangePage(
    IReadOnlyList<SurveyFormSnapshot> Upserts,
    IReadOnlyList<ReferenceTombstone> Tombstones,
    long Cursor);

/// <summary>
/// The survey forms a device should hold, as a delta (<c>OFF-03</c>, W10 slice 7).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scoped by nothing</b>, like the visit workflows: every device in the tenant gets every form.
/// There is nothing here one rep may see and another may not — a questionnaire is a tenant's own
/// administrators' text — and narrowing it would need a rule about which forms a rep might be asked,
/// which nothing knows because nothing yet binds a form to a workflow step (W10 slice 3b).
/// </para>
/// <para>
/// <b>Tombstones are real here.</b> An administrator can delete a form, and the tombstone is
/// tenant-wide so it can go to every device without telling anyone anything about anybody. A device
/// that drops one and is then asked to open it shows a form it does not have — which is the same
/// state as a device that has never synced, and is why an audit carries each question's text rather
/// than relying on the form still existing.
/// </para>
/// </remarks>
public interface ISurveyFormFeed
{
    /// <summary>
    /// Forms whose row version is above <paramref name="cursor"/>, plus tombstones for any deleted
    /// since.
    /// </summary>
    Task<SurveyFormChangePage> GetChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default);
}
