using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Configuration.Contracts;

namespace FieldKit.Server.Tests;

/// <summary>
/// Authoring a tenant's questionnaires over HTTP (<c>AUD-04</c>, <c>CFG-04</c>) — W10 slice 2.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SurveyFormTests"/> covers the rules; this covers what a caller reaches — the refusal
/// codes a screen branches on, the per-question problem paths, the permission split, and tenant
/// isolation.
/// </para>
/// <para>
/// <b>Every form here is named uniquely per test.</b> The name is unique within the tenant and these
/// share a collection and a database, so a fixed name would pass alone and 409 the moment two tests
/// ran in the same session.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class SurveyTests(ServerFixture fixture)
{
    private static SurveyQuestionRequest Question(
        string key, SurveyQuestionType type = SurveyQuestionType.Text, bool mandatory = false,
        IReadOnlyList<string>? options = null) =>
        new(key, $"Question {key}?", type, mandatory, options);

    private static SurveyFormRequest Form(string name) => new(name, [
        Question("chiller_lit", SurveyQuestionType.Boolean, mandatory: true),
        Question("facings", SurveyQuestionType.Number),
        Question("facing_quality", SurveyQuestionType.SingleChoice, options: ["Good", "Poor"]),
    ]);

    private static string Named(string what) => $"{what} {Guid.CreateVersion7()}";

    private static async Task<SurveyFormResponse> CreateAsync(
        HttpClient client, SurveyFormRequest? request = null)
    {
        var response = await client.PostAsJsonAsync(
            "/api/config/surveys", request ?? Form(Named("Survey")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<SurveyFormResponse>())!;
    }

    [Fact]
    public async Task A_survey_is_created_with_its_questions_numbered_from_one()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var form = await CreateAsync(admin);

        Assert.NotEqual(Guid.Empty, form.Id);
        Assert.Equal([1, 2, 3], form.Questions.Select(question => question.Order));
        Assert.Equal("chiller_lit", form.Questions[0].Key);
        Assert.True(form.Questions[0].Mandatory);
        Assert.Equal(["Good", "Poor"], form.Questions[2].Options);
    }

    [Fact]
    public async Task A_question_type_arrives_as_its_name()
    {
        /*
         * Raw JSON, for the reason `VisitWorkflowTests` and `ScoreWeightTests` need it: posting the
         * typed record serialises through the property's own converter, so a request and its
         * assertion would agree with each other whatever the wire format was. The visit workflow's
         * step type was a 400 for every name until its converter was added — only the ordinal `0`
         * worked — and this is the test that would have caught it.
         */
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await admin.PostAsync("/api/config/surveys", new StringContent(
            $$"""
            {
              "name": "{{Named("Raw")}}",
              "questions": [
                { "key": "chiller_lit", "text": "Is the chiller lit?", "type": "Boolean", "mandatory": true },
                { "key": "quality", "text": "Facing quality?", "type": "MultiChoice",
                  "mandatory": false, "options": ["Good", "Poor"] }
              ]
            }
            """,
            Encoding.UTF8,
            "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var form = (await response.Content.ReadFromJsonAsync<SurveyFormResponse>())!;

        // And back out as a name too — a request and its own response disagreeing about how one enum
        // is spelled is the shape of the bug this pair exists for.
        Assert.Equal(nameof(SurveyQuestionType.Boolean), form.Questions[0].Type);
        Assert.Equal(nameof(SurveyQuestionType.MultiChoice), form.Questions[1].Type);
    }

    [Fact]
    public async Task A_plain_question_needs_neither_a_mandatory_flag_nor_options()
    {
        /*
         * `RespectRequiredConstructorParameters` makes a positional parameter without a default
         * required *on the wire* — nullable is not enough. This 400'd until `Options` and `Mandatory`
         * were given defaults, which is the same trap the push wire vectors found in W9 slice 12.
         *
         * Raw JSON again, because a typed request cannot express an omitted property at all.
         */
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await admin.PostAsync("/api/config/surveys", new StringContent(
            $$"""
            {
              "name": "{{Named("Terse")}}",
              "questions": [ { "key": "notes", "text": "Anything to add?", "type": "Text" } ]
            }
            """,
            Encoding.UTF8,
            "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var form = (await response.Content.ReadFromJsonAsync<SurveyFormResponse>())!;

        // Optional by omission, which is the safe direction: mandatory-by-default would block a rep's
        // check-out over a flag nobody typed.
        Assert.False(Assert.Single(form.Questions).Mandatory);
        Assert.Empty(form.Questions[0].Options);
    }

    [Fact]
    public async Task A_survey_with_no_questions_is_refused_by_name()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await admin.PostAsJsonAsync(
            "/api/config/surveys", new SurveyFormRequest(Named("Hollow"), []));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "config.survey.empty", Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_choice_question_with_nothing_to_choose_from_is_refused()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await admin.PostAsJsonAsync("/api/config/surveys", new SurveyFormRequest(
            Named("Unanswerable"),
            [Question("quality", SurveyQuestionType.SingleChoice)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "config.survey.choiceWithoutOptions",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task Two_questions_sharing_a_key_are_refused()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await admin.PostAsJsonAsync("/api/config/surveys", new SurveyFormRequest(
            Named("Duplicated"), [Question("chiller_lit"), Question("chiller_lit")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "config.survey.duplicateKey", Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_bad_question_is_named_by_its_position()
    {
        // An admin looking at twelve questions cannot work out which one "a question needs text" is
        // about. The indexed path is what puts the message beside the control that caused it.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await admin.PostAsJsonAsync("/api/config/surveys", new SurveyFormRequest(
            Named("Malformed"),
            [
                Question("chiller_lit"),
                new SurveyQuestionRequest("Shelf Clean", "", SurveyQuestionType.Text, false, null),
            ]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await Refusals.ProblemsOf(response);

        Assert.Contains(problems, problem =>
            problem.Field == "questions[1].key" && problem.Code == "config.survey.keyMalformed");
        Assert.Contains(problems, problem =>
            problem.Field == "questions[1].text" && problem.Code == "config.survey.textRequired");

        // …and nothing was said about question 0, which was fine.
        Assert.DoesNotContain(problems, problem => problem.Field?.StartsWith("questions[0]") == true);
    }

    [Fact]
    public async Task A_survey_needs_a_name()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await admin.PostAsJsonAsync(
            "/api/config/surveys", new SurveyFormRequest("  ", [Question("chiller_lit")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "config.survey.nameRequired", Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task Two_surveys_cannot_share_a_name()
    {
        // The name is how an admin picks a form off a list, so two of them is a list with two
        // identical rows. A 409 naming it rather than a 500 from the unique index.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var name = Named("Chiller compliance");
        await CreateAsync(admin, Form(name));

        var again = await admin.PostAsJsonAsync("/api/config/surveys", Form(name));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var problem = Assert.Single(await Refusals.ProblemsOf(again));

        Assert.Equal("config.survey.nameTaken", problem.Code);

        /*
         * The name travels as an argument, not only inside the English sentence.
         *
         * A translated catalogue cannot dig a value out of `message`, so without this the reader's
         * language can only say "another survey already has that name" — and a catalogue entry that
         * named a placeholder the server does not send throws inside `next-intl` at render, which is
         * the coupling ADR-0012 names as its cost. `product.priceList.nameTaken` carries the same
         * argument for the same reason; this is asserted rather than assumed because nothing in C#
         * fails when an args dictionary quietly goes missing.
         */
        Assert.NotNull(problem.Args);
        Assert.Equal(name, Assert.Contains("name", problem.Args));
    }

    [Fact]
    public async Task A_survey_can_be_renamed_to_the_name_it_already_has()
    {
        // The bug an exclusion-free uniqueness check ships with: saving a form without touching its
        // name would find itself and refuse.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var name = Named("Unchanged");
        var form = await CreateAsync(admin, Form(name));

        var response = await admin.PutAsJsonAsync($"/api/config/surveys/{form.Id}", Form(name));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Editing_replaces_the_questions_and_keeps_the_id()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var form = await CreateAsync(admin);

        var response = await admin.PutAsJsonAsync(
            $"/api/config/surveys/{form.Id}",
            new SurveyFormRequest(Named("Reworked"), [
                Question("poster_up", SurveyQuestionType.Boolean),
                Question("chiller_lit", SurveyQuestionType.Boolean, mandatory: true),
            ]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = (await response.Content.ReadFromJsonAsync<SurveyFormResponse>())!;

        Assert.Equal(form.Id, updated.Id);
        Assert.Equal(["poster_up", "chiller_lit"], updated.Questions.Select(question => question.Key));
        Assert.Equal([1, 2], updated.Questions.Select(question => question.Order));

        // Read back, because the write path and the read path are different queries — and the whole
        // reason questions are re-Added to the context is that one of them silently did nothing.
        var read = await admin.GetFromJsonAsync<SurveyFormResponse>($"/api/config/surveys/{form.Id}");

        Assert.Equal(["poster_up", "chiller_lit"], read!.Questions.Select(question => question.Key));
    }

    [Fact]
    public async Task A_survey_can_be_deleted()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var form = await CreateAsync(admin);

        var deleted = await admin.DeleteAsync($"/api/config/surveys/{form.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var read = await admin.GetAsync($"/api/config/surveys/{form.Id}");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    [Fact]
    public async Task A_survey_nobody_defined_is_not_found()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await admin.GetAsync($"/api/config/surveys/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Surveys_are_listed_with_their_questions()
    {
        // The list carries the questions, because the contract's AllAsync includes them: a screen
        // showing "12 questions" beside each form should not need a request per row.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var form = await CreateAsync(admin);

        var all = await admin.GetFromJsonAsync<List<SurveyFormResponse>>("/api/config/surveys");

        Assert.Equal(3, Assert.Single(all!, candidate => candidate.Id == form.Id).Questions.Count);
    }

    [Fact]
    public async Task Reading_a_survey_needs_a_permission_and_changing_one_needs_another()
    {
        // A rep will sync forms through the pull feed, not through this API; the authoring surface is
        // an administrator's. `read-only` holds config:read and not config:write.
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        var read = await viewer.GetAsync("/api/config/surveys");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var write = await viewer.PostAsJsonAsync("/api/config/surveys", Form(Named("Forbidden")));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task A_survey_belongs_to_its_tenant_and_no_other()
    {
        // The isolation gate, asserted rather than assumed.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var other = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var ours = await CreateAsync(admin);

        var listed = await other.GetFromJsonAsync<JsonElement>("/api/config/surveys");

        Assert.DoesNotContain(
            listed.EnumerateArray(), form => form.GetProperty("id").GetGuid() == ours.Id);

        var directly = await other.GetAsync($"/api/config/surveys/{ours.Id}");
        Assert.Equal(HttpStatusCode.NotFound, directly.StatusCode);
    }

    [Fact]
    public async Task Two_tenants_can_use_the_same_survey_name()
    {
        // The uniqueness is per tenant, which the composite index says and this proves — the check in
        // the endpoint runs through the tenant filter, so a name taken next door is not taken here.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var other = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var name = Named("Shared name");

        await CreateAsync(admin, Form(name));
        await CreateAsync(other, Form(name));
    }
}
