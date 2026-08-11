using FieldKit.Modules.Configuration;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.SharedKernel;

namespace FieldKit.Server.Tests;

/// <summary>
/// A tenant's questionnaire, as a rule rather than as an endpoint (<c>AUD-04</c>, <c>CFG-04</c>) —
/// W10 slice 2.
/// </summary>
/// <remarks>
/// <para>
/// Three properties carry the slice: the order is the submitted order (assigned, never accepted), a
/// key is unique because an answer is filed under it, and a choice question offers something to
/// choose from. None of them needs a database to be wrong.
/// </para>
/// <para>
/// <see cref="SurveyTests"/> covers what a caller sees over HTTP.
/// </para>
/// </remarks>
public class SurveyFormTests
{
    /// <summary>A clock that does not move. Time is incidental here — every rule is about shape.</summary>
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private static readonly IClock Clock = new FixedClock(new DateTimeOffset(2026, 4, 6, 9, 0, 0, TimeSpan.Zero));

    private static SurveyQuestionDraft Question(
        string key, SurveyQuestionType type = SurveyQuestionType.Text, bool mandatory = false,
        IReadOnlyList<string>? options = null) =>
        new(key, $"Question {key}?", type, mandatory, options);

    [Fact]
    public void A_form_is_created_with_its_questions_in_the_order_they_were_sent()
    {
        // Order is assigned from the position in the list, never accepted: a caller that sent its own
        // numbers could send 1, 2, 2, 7, and every consumer would then have to decide what a gap or a
        // tie means. The visit workflow made this call first.
        var (form, refusal) = SurveyForm.Create("Chiller compliance", [
            Question("chiller_lit"),
            Question("shelf_clean"),
            Question("poster_up"),
        ]);

        Assert.Equal(SurveyFormRefusal.None, refusal);
        Assert.NotNull(form);
        Assert.Equal("Chiller compliance", form.Name);
        Assert.Equal([1, 2, 3], form.Questions.OrderBy(q => q.Order).Select(q => q.Order));
        Assert.Equal(
            ["chiller_lit", "shelf_clean", "poster_up"],
            form.Questions.OrderBy(q => q.Order).Select(q => q.Key));
    }

    [Fact]
    public void A_form_with_no_questions_is_refused()
    {
        // Unlike a visit workflow, where an empty step list is a real thing — a presence call. A form
        // with no questions is a screen that opens and offers nothing to do.
        var (form, refusal) = SurveyForm.Create("Empty", []);

        Assert.Equal(SurveyFormRefusal.Empty, refusal);
        Assert.Null(form);
    }

    [Fact]
    public void Two_questions_cannot_share_a_key()
    {
        /*
         * The rule the whole key idea rests on. An answer is filed under the key, so two questions
         * sharing one means two answers under one name — and the reader has no way to tell which
         * question was being answered.
         */
        var (form, refusal) = SurveyForm.Create("Duplicated", [
            Question("chiller_lit"),
            Question("chiller_lit"),
        ]);

        Assert.Equal(SurveyFormRefusal.DuplicateKey, refusal);
        Assert.Null(form);
    }

    [Theory]
    [InlineData(SurveyQuestionType.SingleChoice)]
    [InlineData(SurveyQuestionType.MultiChoice)]
    public void A_choice_question_needs_something_to_choose_from(SurveyQuestionType type)
    {
        // Both choice types, because "which types have options" is a question three places ask and
        // the third one comparing against SingleChoice alone is exactly how a multi-choice question
        // ends up stored with its options thrown away.
        var (_, refusal) = SurveyForm.Create("Unanswerable", [Question("facing_quality", type)]);

        Assert.Equal(SurveyFormRefusal.ChoiceWithoutOptions, refusal);
    }

    [Fact]
    public void A_choice_question_with_only_blank_options_is_refused_too()
    {
        // A list of empty strings is a control with nothing in it, which is the thing being refused —
        // the count of the list is not the test.
        var (_, refusal) = SurveyForm.Create(
            "Blank", [Question("facing_quality", SurveyQuestionType.SingleChoice, options: ["", "  "])]);

        Assert.Equal(SurveyFormRefusal.ChoiceWithoutOptions, refusal);
    }

    [Fact]
    public void Options_are_kept_for_a_choice_and_dropped_for_everything_else()
    {
        /*
         * Options on a non-choice would render nowhere and constrain nothing — and would become
         * quietly authoritative again the moment somebody switched the type back. The trap
         * `FieldDefinition` already documents, and the same fix.
         */
        var (form, _) = SurveyForm.Create("Mixed", [
            Question("facing_quality", SurveyQuestionType.SingleChoice, options: ["Good", "Poor"]),
            Question("notes", SurveyQuestionType.Text, options: ["Good", "Poor"]),
        ]);

        Assert.Equal(["Good", "Poor"], form!.Questions.Single(q => q.Key == "facing_quality").Options);
        Assert.Empty(form.Questions.Single(q => q.Key == "notes").Options);
    }

    [Fact]
    public void Duplicate_options_within_one_question_are_collapsed()
    {
        // Two identical radio buttons is not a choice a rep can make meaningfully, and the answer
        // stored would be the same string either way.
        var (form, _) = SurveyForm.Create(
            "Repeated",
            [Question("facing_quality", SurveyQuestionType.SingleChoice, options: ["Good", "Good", "Poor"])]);

        Assert.Equal(["Good", "Poor"], form!.Questions.Single().Options);
    }

    [Fact]
    public void A_form_asks_at_most_the_maximum()
    {
        var questions = Enumerable.Range(1, SurveyForm.MaximumQuestions + 1)
            .Select(index => Question($"q{index}"))
            .ToList();

        var (_, refusal) = SurveyForm.Create("Endless", questions);

        Assert.Equal(SurveyFormRefusal.TooManyQuestions, refusal);
    }

    [Fact]
    public void Editing_replaces_the_questions_wholesale_and_renumbers_them()
    {
        /*
         * Wholesale, never patched — an ordered thing cannot be patched without the caller knowing
         * the current order, and two admins editing one form would interleave into a sequence
         * neither designed.
         *
         * The consequence this asserts is the one that justifies the key: the surviving question is
         * a *new row* with a new id, at a new position, and only its key is the same.
         */
        var (form, _) = SurveyForm.Create("Original", [
            Question("chiller_lit"),
            Question("shelf_clean"),
        ]);

        var originalId = form!.Questions.Single(q => q.Key == "shelf_clean").Id;

        var refusal = form.Set("Renamed", [
            Question("poster_up"),
            Question("shelf_clean"),
        ], Clock);

        Assert.Equal(SurveyFormRefusal.None, refusal);
        Assert.Equal("Renamed", form.Name);
        Assert.Equal(["poster_up", "shelf_clean"], form.Questions.OrderBy(q => q.Order).Select(q => q.Key));

        var survivor = form.Questions.Single(q => q.Key == "shelf_clean");

        Assert.Equal(2, survivor.Order);
        Assert.NotEqual(originalId, survivor.Id);
    }

    [Fact]
    public void A_refused_edit_leaves_the_form_alone()
    {
        // The half that is easy to miss: a refusal that had already cleared the questions would leave
        // a form nobody asked for — and, being row-versioned, would sync that form to every device.
        var (form, _) = SurveyForm.Create("Original", [
            Question("chiller_lit"),
            Question("shelf_clean"),
        ]);

        var refusal = form!.Set("Broken", [Question("poster_up"), Question("poster_up")], Clock);

        Assert.Equal(SurveyFormRefusal.DuplicateKey, refusal);
        Assert.Equal("Original", form.Name);
        Assert.Equal(["chiller_lit", "shelf_clean"], form.Questions.OrderBy(q => q.Order).Select(q => q.Key));
    }

    [Fact]
    public void Mandatory_lives_on_the_question()
    {
        // BR-AUD-7 gates the audit step on the mandatory ones. The flag is per question rather than
        // per type because the same question is required on one form and a courtesy on another.
        var (form, _) = SurveyForm.Create("Mixed", [
            Question("chiller_lit", mandatory: true),
            Question("notes"),
        ]);

        Assert.True(form!.Questions.Single(q => q.Key == "chiller_lit").Mandatory);
        Assert.False(form.Questions.Single(q => q.Key == "notes").Mandatory);
    }

    [Fact]
    public void Describing_a_form_orders_the_questions_and_carries_the_keys()
    {
        // What another module actually reads. The descriptor is ordered here rather than by every
        // caller, because a form arriving out of order is a questionnaire asked backwards.
        var (form, _) = SurveyForm.Create("Chiller compliance", [
            Question("chiller_lit", mandatory: true),
            Question("facing_quality", SurveyQuestionType.SingleChoice, options: ["Good", "Poor"]),
        ]);

        var described = form!.Describe();

        Assert.Equal(form.Id, described.Id);
        Assert.Equal("Chiller compliance", described.Name);
        Assert.Equal([1, 2], described.Questions.Select(q => q.Order));
        Assert.Equal("chiller_lit", described.Questions[0].Key);
        Assert.True(described.Questions[0].Mandatory);
        Assert.Equal(["Good", "Poor"], described.Questions[1].Options);
    }

    [Fact]
    public void Both_choice_types_are_choices_and_nothing_else_is()
    {
        // The helper every other rule branches on, asserted directly so that adding a seventh type
        // has to answer this question rather than inherit an answer.
        Assert.True(SurveyQuestionType.SingleChoice.IsChoice());
        Assert.True(SurveyQuestionType.MultiChoice.IsChoice());

        foreach (var type in Enum.GetValues<SurveyQuestionType>()
                     .Except([SurveyQuestionType.SingleChoice, SurveyQuestionType.MultiChoice]))
        {
            Assert.False(type.IsChoice());
        }
    }
}
