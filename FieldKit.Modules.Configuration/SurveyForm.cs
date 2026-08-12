using System.Text.Json.Serialization;
using FieldKit.BuildingBlocks;
using FieldKit.Infrastructure;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Configuration;

/// <summary>One question on a tenant's survey form (<c>AUD-04</c>, <c>CFG-04</c>).</summary>
public sealed class SurveyQuestion : ITenantOwned
{
    /// <summary>The column width for a question's text.</summary>
    public const int MaximumTextLength = 300;

    /// <summary>The column width for a question's key.</summary>
    public const int MaximumKeyLength = 60;

    private readonly List<string> _options = [];

    public Guid Id { get; private set; }

    public Guid SurveyFormId { get; private set; }

    /// <summary>Where it sits in the form. Contiguous from 1 — see <see cref="SurveyForm"/>.</summary>
    public int Order { get; private set; }

    /// <summary>
    /// What an answer is filed under.
    /// </summary>
    /// <remarks>
    /// <b>The reason a question has a key at all, when a visit workflow's step does not.</b> Nothing
    /// stores a reference to a workflow step — a visit copies it. An <i>answer</i> is different: it
    /// outlives the audit that produced it and is what <c>AUD-09</c>'s reporting reads, so "how did
    /// reps answer the chiller question last quarter?" has to survive the form being re-authored.
    /// The questions are replaced wholesale on every edit and their ids are regenerated with them, so
    /// an id would answer that question with a dangling pointer. A key survives reorders, rewordings
    /// and re-authoring — the same bargain <see cref="FieldDefinition.Key"/> makes, for the same
    /// reason.
    /// </remarks>
    public string Key { get; private set; } = null!;

    /// <summary>What the rep is asked. For their screen; never for matching.</summary>
    public string Text { get; private set; } = null!;

    public SurveyQuestionType Type { get; private set; }

    /// <summary>Whether the audit step is refused while it is unanswered (<c>BR-AUD-7</c>).</summary>
    public bool Mandatory { get; private set; }

    /// <summary>The permitted answers for a choice question; empty otherwise.</summary>
    public IReadOnlyList<string> Options => _options;

    public TenantId TenantId { get; set; }

    private SurveyQuestion() { } // EF

    internal static SurveyQuestion Create(
        Guid formId, int order, string key, string text, SurveyQuestionType type, bool mandatory,
        IEnumerable<string>? options)
    {
        var question = new SurveyQuestion
        {
            Id = Guid.CreateVersion7(),
            SurveyFormId = formId,
            Order = order,
            Key = key.Trim(),
            Text = text.Trim(),
            Type = type,
            Mandatory = mandatory,
        };

        // Options only mean something for a choice. Kept on anything else they would render nowhere,
        // constrain nothing, and become quietly authoritative again the moment somebody switched the
        // type back — the trap `FieldDefinition` already documents.
        if (type.IsChoice() && options is not null)
        {
            question._options.AddRange(options
                .Select(option => option.Trim())
                .Where(option => option.Length > 0)
                .Distinct(StringComparer.Ordinal));
        }

        return question;
    }
}

/// <summary>Why a survey form was refused. <see cref="None"/> means it was not.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SurveyFormRefusal>))]
public enum SurveyFormRefusal
{
    None,

    /// <summary>No questions. A form that asks nothing is a screen a rep cannot complete.</summary>
    Empty,

    /// <summary>More questions than <see cref="SurveyForm.MaximumQuestions"/>.</summary>
    TooManyQuestions,

    /// <summary>Two questions share a key, so two answers would be filed under one name.</summary>
    DuplicateKey,

    /// <summary>A choice question with nothing to choose from.</summary>
    ChoiceWithoutOptions,
}

/// <summary>
/// A tenant-defined questionnaire (<c>AUD-04</c>, <c>CFG-04</c>, <c>A1</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Named and identified, not keyed by channel.</b> The visit workflow is keyed by channel because
/// a channel has exactly one answer to "how is a visit worked here". A tenant genuinely has several
/// questionnaires at once — a standing compliance form and a quarterly brand survey — so a form has
/// an id and a name a person can pick from a list.
/// </para>
/// <para>
/// <b>Nothing points at a form yet, and that is deliberate.</b> A <c>Survey</c> step in a visit
/// workflow names no form today; how an audit chooses one is W10 slice 3's decision, taken with the
/// module that has to live with it. Binding a step to a form now would mean changing
/// <see cref="VisitStepDescriptor"/> — a public contract — to serve a consumer that does not exist.
/// </para>
/// <para>
/// <b>Questions are replaced wholesale, never patched</b>, and their order is assigned here rather
/// than accepted — both for the reasons <see cref="VisitWorkflow"/> gives. The consequence specific
/// to a form is that question ids are regenerated on every edit, which is why an answer is filed
/// under <see cref="SurveyQuestion.Key"/> and not under an id.
/// </para>
/// <para>
/// <b>No version numbers, unlike <see cref="ScoreWeightSet"/>.</b> The weights are versioned because
/// <c>BR-AUD-8</c> has the server recompute a sealed audit with the exact numbers it was scored
/// against — an arithmetic promise that needs a frozen input. A form has no such promise to keep: an
/// audit stores the answers it was given, together with the question text as it was asked, so a form
/// edited afterwards changes what is asked next rather than what was recorded. Row-versioned for
/// sync like the visit workflow, and that is all it needs.
/// </para>
/// </remarks>
public sealed class SurveyForm : AggregateRoot, ITenantOwned, IAuditable, ISyncTracked
{
    /// <summary>The column width for a form's name.</summary>
    public const int MaximumNameLength = 120;

    /// <summary>
    /// The most questions one form can ask.
    /// </summary>
    /// <remarks>
    /// A sanity bound, and a lower one than a visit workflow's for a reason that is not symmetry: a
    /// rep works a workflow across a whole call, but answers a form standing at a shelf in one go.
    /// Fifty questions there is a configuration mistake, and the cost of finding out is a rep
    /// abandoning the step.
    /// </remarks>
    public const int MaximumQuestions = 50;

    /// <summary>Set by the row-version interceptor, never here (ADR-0013).</summary>
    /// <remarks>
    /// On the root only, for the reason <see cref="VisitWorkflow.RowVersion"/> gives: a question is
    /// not something a device holds separately, it is part of the form it arrives with. Every edit
    /// goes through <see cref="Set"/>, which writes <c>ModifiedAtUtc</c> and so marks this row
    /// modified whatever the questions did.
    /// </remarks>
    public long RowVersion { get; set; }

    private readonly List<SurveyQuestion> _questions = [];

    public Guid Id { get; private set; }

    /// <summary>What an admin calls it, and what they pick it by. Unique within the tenant.</summary>
    public string Name { get; private set; } = null!;

    public IReadOnlyList<SurveyQuestion> Questions => _questions;

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private SurveyForm() { } // EF

    public static (SurveyForm? Form, SurveyFormRefusal Refusal) Create(
        string name, IReadOnlyList<SurveyQuestionDraft> questions)
    {
        if (Check(questions) is var refusal && refusal is not SurveyFormRefusal.None)
        {
            return (null, refusal);
        }

        var form = new SurveyForm { Id = Guid.CreateVersion7(), Name = name.Trim() };
        form.Replace(questions);

        return (form, SurveyFormRefusal.None);
    }

    /// <summary>Renames the form and replaces its questions.</summary>
    public SurveyFormRefusal Set(
        string name, IReadOnlyList<SurveyQuestionDraft> questions, IClock clock)
    {
        if (Check(questions) is var refusal && refusal is not SurveyFormRefusal.None) return refusal;

        Name = name.Trim();
        Replace(questions);
        ModifiedAtUtc = clock.UtcNow;

        return SurveyFormRefusal.None;
    }

    /// <summary>This form as another module sees it.</summary>
    public SurveyFormDescriptor Describe() => new(
        Id,
        Name,
        [.. _questions
            .OrderBy(question => question.Order)
            .Select(question => new SurveyQuestionDescriptor(
                question.Order,
                question.Key,
                question.Text,
                question.Type,
                question.Mandatory,
                question.Options))]);

    /// <summary>
    /// Whether a set of questions is one this module will store.
    /// </summary>
    /// <remarks>
    /// Only the rules a form cannot be without. Whether a question's text is present and short enough
    /// is the endpoint's to answer, because it can say <i>which</i> question — an aggregate returning
    /// "a question needs text" leaves an admin looking at eight of them.
    /// </remarks>
    private static SurveyFormRefusal Check(IReadOnlyList<SurveyQuestionDraft> questions)
    {
        // Unlike a visit workflow, where an empty step list is a real thing — a presence call. A form
        // with no questions is a screen that opens and offers nothing to do.
        if (questions.Count == 0) return SurveyFormRefusal.Empty;

        if (questions.Count > MaximumQuestions) return SurveyFormRefusal.TooManyQuestions;

        // Ordinal, and case-sensitively distinct keys are still distinct: the key is an identifier
        // going into JSON, not prose, and the endpoint refuses anything but lowercase anyway.
        if (questions.Select(question => question.Key.Trim()).Distinct(StringComparer.Ordinal).Count()
            != questions.Count)
        {
            return SurveyFormRefusal.DuplicateKey;
        }

        // A choice with nothing to choose from is not an optional detail: the rep gets a control with
        // no answers in it, and a mandatory one would make the step impossible to finish.
        if (questions.Any(question => question.Type.IsChoice()
            && (question.Options is null || !question.Options.Any(option => !string.IsNullOrWhiteSpace(option)))))
        {
            return SurveyFormRefusal.ChoiceWithoutOptions;
        }

        return SurveyFormRefusal.None;
    }

    private void Replace(IReadOnlyList<SurveyQuestionDraft> questions)
    {
        _questions.Clear();

        var order = 1;

        foreach (var question in questions)
        {
            _questions.Add(SurveyQuestion.Create(
                Id, order++, question.Key, question.Text, question.Type, question.Mandatory,
                question.Options));
        }
    }
}

/// <summary>One question as a caller submits it — no order, position in the list is the order.</summary>
public sealed record SurveyQuestionDraft(
    string Key,
    string Text,
    SurveyQuestionType Type,
    bool Mandatory,
    IReadOnlyList<string>? Options);
