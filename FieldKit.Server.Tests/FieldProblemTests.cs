using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Outlets;
using FieldKit.Web;

namespace FieldKit.Server.Tests;

/// <summary>
/// What a refused write says about <i>where</i> it went wrong (api-contracts §3).
/// </summary>
/// <remarks>
/// Endpoints used to answer with prose — <c>{ "error": "A territory needs a name." }</c> — which
/// reads perfectly and tells a form nothing about which control to put it under. These tests are
/// about the field, because the message was never the part that was missing.
/// </remarks>
[Collection(ServerCollection.Name)]
public class FieldProblemTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private sealed record Refusal(IReadOnlyList<FieldProblem> Errors);

    /// <summary>The problems a response carried, whatever its status.</summary>
    private static async Task<IReadOnlyList<FieldProblem>> ProblemsOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Refusal>())?.Errors ?? [];

    private async Task<Guid> ChannelAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        return (await response.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    [Fact]
    public async Task Every_refusal_uses_one_envelope()
    {
        // A 400 and a 409 are different answers about different things, but a client should read
        // them the same way — one branch, not two shapes to sniff between.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var channelId = await ChannelAsync(client);
        var code = Unique("OUT");

        await client.PostAsJsonAsync(
            "/api/outlets", new CreateOutletRequest(code, "First", channelId, null, null, Zone));

        var badRequest = await client.PostAsJsonAsync(
            "/api/outlets", new CreateOutletRequest("", "No code", channelId, null, null, Zone));

        var conflict = await client.PostAsJsonAsync(
            "/api/outlets", new CreateOutletRequest(code, "Duplicate", channelId, null, null, Zone));

        Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        Assert.Equal("code", Assert.Single(await ProblemsOf(badRequest)).Field);
        Assert.Equal("code", Assert.Single(await ProblemsOf(conflict)).Field);
    }

    [Fact]
    public async Task A_refusal_names_the_field_the_caller_sent()
    {
        // The path in *their* request, not a column or a form control — the caller sent `channelId`,
        // so `channelId` is what it is told about.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var unknownChannel = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", Guid.CreateVersion7(), null, null, Zone));

        Assert.Equal("channelId", Assert.Single(await ProblemsOf(unknownChannel)).Field);

        var badZone = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(
                Unique("OUT"), "Corner Shop", await ChannelAsync(client), null, null, "Mars/Olympus_Mons"));

        Assert.Equal("timeZoneId", Assert.Single(await ProblemsOf(badZone)).Field);
    }

    [Fact]
    public async Task A_custom_field_is_named_by_the_path_it_was_sent_under()
    {
        // `customFields.chiller_count`, not `chiller_count`. The request has a `customFields` object,
        // so that is where a client looks — and the bare key would collide with a fixed field the
        // day a tenant defines one called `name`.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var key = $"k{Guid.NewGuid():N}"[..12];

        await client.PostAsJsonAsync("/api/config/field-definitions", new CreateFieldDefinitionRequest(
            CustomFieldEntity.Outlet, key, "Chillers", CustomFieldType.Number, Minimum: 0, Maximum: 50));

        var response = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(
                Unique("OUT"), "Corner Shop", await ChannelAsync(client), null, null, Zone,
                CustomFields: new Dictionary<string, JsonElement>
                {
                    [key] = JsonSerializer.SerializeToElement(900),
                }));

        var problem = Assert.Single(await ProblemsOf(response));

        Assert.Equal($"customFields.{key}", problem.Field);
        Assert.Contains("at most 50", problem.Message);
    }

    [Fact]
    public async Task A_problem_about_no_field_in_particular_says_so()
    {
        // Null rather than a guessed field. "The file has a header but no rows" is about the upload,
        // and a form given a field name for it would highlight a control at random.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await client.PostAsync(
            "/api/outlets/import",
            new StringContent("code,name,channel,time_zone", System.Text.Encoding.UTF8, "text/csv"));

        var problem = Assert.Single(await ProblemsOf(response));

        Assert.Null(problem.Field);
        Assert.Contains("no rows", problem.Message);
    }

    [Fact]
    public async Task Every_problem_with_a_request_comes_back_at_once()
    {
        // One pass over a form, not one round trip per mistake. The user endpoint checks six things
        // and this asserts it reports all of the failing ones rather than stopping at the first.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await client.PostAsJsonAsync(
            "/api/iam/users",
            new UserRequest("", "", "", "not-a-locale", "Mars/Olympus_Mons", []));

        var problems = await ProblemsOf(response);
        var fields = problems.Select(problem => problem.Field).ToList();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("subjectId", fields);
        Assert.Contains("email", fields);
        Assert.Contains("displayName", fields);
        Assert.Contains("locale", fields);
        Assert.Contains("timeZone", fields);
        Assert.Contains("roleIds", fields);
    }
}
