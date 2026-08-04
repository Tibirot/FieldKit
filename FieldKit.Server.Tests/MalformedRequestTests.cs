using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Outlets;
using FieldKit.Server;
using Microsoft.AspNetCore.Http;

namespace FieldKit.Server.Tests;

/// <summary>
/// What the API says when a request body cannot be read at all.
/// </summary>
/// <remarks>
/// <para>
/// A body the server cannot parse is the caller's mistake, and 400 is the answer that says so. 500
/// says the opposite — that the server broke — and it is the difference between a client developer
/// fixing their payload in a minute and filing a bug against an API that looks unreliable. It also
/// costs something concrete here: 500s page someone, and a bad enum name in a device's sync payload
/// would page them at 3am for a typo.
/// </para>
/// <para>
/// Enums are where this bites, because they are the one place FieldKit deliberately puts a closed
/// vocabulary on the wire (<see cref="OutletStatusRequest"/> and the field-definition contracts both
/// travel by name, not ordinal). Every one of those names is a value a caller can get wrong.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class MalformedRequestTests(ServerFixture fixture)
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<Guid> OutletAsync(HttpClient client)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest($"CH-{Guid.NewGuid():N}"[..20]));
        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var outlet = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(
                $"OUT-{Guid.NewGuid():N}"[..20], "Corner Shop", channelId, null, null, "UTC"));

        return (await outlet.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    [Fact]
    public async Task A_status_that_is_not_a_status_is_the_callers_mistake()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var id = await OutletAsync(client);

        var response = await client.PostAsync(
            $"/api/outlets/{id}/status", Json("""{"status":"Nonsense","reason":"x"}"""));

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"expected 400, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task A_field_type_that_is_not_a_type_is_the_callers_mistake()
    {
        // The same shape in a different module, because the fix has to be the host's rather than one
        // endpoint's — every module that puts an enum on the wire inherits this.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await client.PostAsync(
            "/api/config/field-definitions",
            Json("""{"entity":"Outlet","key":"k","label":"L","type":"Sasquatch"}"""));

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"expected 400, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task So_is_a_body_that_is_not_JSON()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await client.PostAsync("/api/outlets/channels", Json("{ this is not json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // No path here: the parser reports "$" — the body itself — and telling a caller their "$" is
        // wrong names something they did not write.
        Assert.Contains("could not be read as JSON", body);
        Assert.DoesNotContain("$", body);
    }

    [Fact]
    public async Task And_a_value_of_the_wrong_shape_entirely()
    {
        // A JSON array where an object belongs — parses as JSON, cannot become the request.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await client.PostAsync("/api/outlets/channels", Json("""["not","an","object"]"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("could not be read as JSON", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_rejected_body_says_which_field_was_wrong()
    {
        // The status code alone leaves a caller guessing which of six fields to look at, so the detail
        // names the one that failed — and names it the way the caller wrote it, as a JSON path.
        //
        // Not the parser's own message: that names the .NET type it could not construct, which tells
        // a caller nothing they can act on and tells everyone else how the server is assembled.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await client.PostAsync(
            "/api/config/field-definitions",
            Json("""{"entity":"Outlet","key":"k","label":"L","type":"Sasquatch"}"""));

        var body = await response.Content.ReadAsStringAsync();

        // The status belongs in this assertion too. Without it the test passes against a 500 that
        // happens to carry the right detail — which is exactly what the broken pipeline produced.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("$.type", body);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FieldKit.Modules", body);
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest, StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status415UnsupportedMediaType, StatusCodes.Status415UnsupportedMediaType)]
    public void A_request_exception_keeps_the_status_it_carries(int carried, int expected) =>
        Assert.Equal(expected, ProblemDetailsExtensions.StatusCodeFor(
            new BadHttpRequestException("bad", carried)));

    [Fact]
    public void A_server_fault_is_still_a_server_fault()
    {
        // The guard against fixing this too hard. Widening the rule until nothing 500s would trade a
        // misreported client error for a worse bug: a genuine fault reported as the caller's problem
        // is a fault nobody ever investigates. Asserted on the rule itself, because the honest
        // integration version needs an endpoint that throws — a production surface no caller should
        // find.
        Assert.Equal(
            StatusCodes.Status500InternalServerError,
            ProblemDetailsExtensions.StatusCodeFor(new InvalidOperationException("the database is gone")));

        Assert.Equal(
            StatusCodes.Status500InternalServerError,
            ProblemDetailsExtensions.StatusCodeFor(new JsonException("thrown by our own code, not the binder")));
    }

    [Fact]
    public async Task A_body_that_reads_fine_still_reaches_the_endpoints_own_rules()
    {
        // The guard against over-correction: turning every failure into 400 at the host would swallow
        // real 500s. A body that parses must still get the endpoint's answer, not the pipeline's.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        var id = await OutletAsync(client);

        var response = await client.PostAsync(
            $"/api/outlets/{id}/status", Json("""{"status":"Closed"}"""));

        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("requires a reason", body);
    }
}
