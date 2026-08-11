namespace FieldKit.Modules.Configuration.Contracts;

/// <summary>
/// What kind of answer a survey question takes (<c>AUD-04</c>).
/// </summary>
/// <remarks>
/// A closed set, for the reason <see cref="CustomFieldType"/> and <see cref="VisitStepType"/> are
/// closed: each of these is a control the device has to render and an answer shape something has to
/// store, so "which kinds exist" is a question the type system should answer rather than one a typo
/// could extend. The list is the spec's own (§3).
/// </remarks>
public enum SurveyQuestionType
{
    /// <summary>Free text.</summary>
    Text = 0,

    /// <summary>A number — a count, a temperature, a shelf height.</summary>
    Number = 1,

    /// <summary>Yes or no.</summary>
    Boolean = 2,

    /// <summary>One of the offered options.</summary>
    SingleChoice = 3,

    /// <summary>Any number of the offered options.</summary>
    MultiChoice = 4,

    /// <summary>
    /// A photo.
    /// </summary>
    /// <remarks>
    /// Named now although the upload path is W11 (<c>OFF-08</c>), for the reason
    /// <see cref="VisitStepType.Photo"/> is: a form an admin cannot express is a form they will
    /// express badly with the types that do exist — "describe the display" as free text, which no
    /// report can ever read.
    /// </remarks>
    Photo = 5,
}

/// <summary>Whether a question type offers a list to pick from.</summary>
public static class SurveyQuestionTypes
{
    /// <summary>
    /// True for the choice types.
    /// </summary>
    /// <remarks>
    /// Here rather than at each call site so "which types have options" has one answer. Every place
    /// that asks — validation, storage, and the control the device renders — has to agree, and a
    /// third caller comparing against <c>SingleChoice</c> alone is how a multi-choice question ends
    /// up stored with its options thrown away.
    /// </remarks>
    public static bool IsChoice(this SurveyQuestionType type) =>
        type is SurveyQuestionType.SingleChoice or SurveyQuestionType.MultiChoice;
}

/// <summary>One question, as the module running the audit sees it.</summary>
/// <param name="Order">Where it sits. Contiguous from 1.</param>
/// <param name="Key">
/// What an answer is filed under. Stable across re-authoring — see <c>SurveyQuestion.Key</c> for why
/// an id would not be.
/// </param>
/// <param name="Text">What the rep is asked. For their screen, never for matching.</param>
/// <param name="Mandatory">
/// Whether the audit step is refused while it is unanswered (<c>BR-AUD-7</c>). On the question rather
/// than the type, because the same question is required on one form and a courtesy on another.
/// </param>
/// <param name="Options">
/// What a choice question offers; empty for every other type. A closed list rather than free text so
/// that <c>AUD-09</c> can count answers rather than read them.
/// </param>
public sealed record SurveyQuestionDescriptor(
    int Order,
    string Key,
    string Text,
    SurveyQuestionType Type,
    bool Mandatory,
    IReadOnlyList<string> Options);

/// <summary>A tenant's questionnaire, as the module running the audit sees it.</summary>
public sealed record SurveyFormDescriptor(
    Guid Id, string Name, IReadOnlyList<SurveyQuestionDescriptor> Questions);

/// <summary>
/// The survey forms a tenant has defined (<c>AUD-04</c>, <c>CFG-04</c>, <c>A1</c>).
/// </summary>
/// <remarks>
/// <para>
/// Configuration owns what a tenant may flex; Audit owns what happens when a rep answers. This is the
/// seam, built one slice ahead of its consumer exactly as <see cref="IVisitWorkflow"/> was — the
/// Audit aggregate lands in W10 slice 3 and needs somewhere to ask what a form asks.
/// </para>
/// <para>
/// <b>One slice ahead, not five.</b> Worth saying because <c>IScoreWeights</c> was deliberately left
/// out of slice 1 under "an interface waits for its caller". The rule is about designing a shape
/// against a consumer nobody has thought about yet; a consumer being written next is a consumer whose
/// needs are known.
/// </para>
/// <para>
/// <b>Returns null for a form nobody defined</b>, unlike <see cref="IVisitWorkflow"/>, which answers
/// with a default. The difference is that there is no sensible default questionnaire: an empty form
/// is refused at authoring precisely because it is a screen that asks nothing, so inventing one here
/// to avoid a null-check would hand the caller a thing the module will not store.
/// </para>
/// </remarks>
public interface ISurveyForms
{
    /// <summary>The form with this id, or null if the tenant has no such form.</summary>
    Task<SurveyFormDescriptor?> ByIdAsync(Guid formId, CancellationToken cancellationToken = default);

    /// <summary>Every form this tenant has defined, by name.</summary>
    Task<IReadOnlyList<SurveyFormDescriptor>> AllAsync(CancellationToken cancellationToken = default);
}
