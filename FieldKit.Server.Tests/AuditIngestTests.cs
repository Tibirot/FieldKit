using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FieldKit.Modules.Audit;
using FieldKit.Modules.Audit.Contracts;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Visit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// Applying an audit a device captured offline (<c>BR-AUD-6</c>, <c>OFF-04</c>) — W10 slice 3a.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AuditRecordTests"/> covers what a stored audit must be true of. What is asserted here
/// is everything the aggregate cannot see: that the visit exists and is this rep's, that a sealed
/// visit refuses a new audit, that a replay is success, and that the read endpoints answer with what
/// was stored.
/// </para>
/// <para>
/// <b>Why this calls the service rather than an endpoint.</b> There is no HTTP write path, on
/// purpose — an audit is worked at a shelf with no signal and arrives through <c>/sync/push</c>,
/// which is wired in W10 slice 6. So these resolve <see cref="IAuditIngest"/> from the running
/// server's own container, which means standing up a tenant context by hand: see
/// <see cref="AsAsync"/>.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class AuditIngestTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    /// <summary>A shop on Calea Dorobanți, and a doorway to stand in.</summary>
    private static readonly Coordinates Shop = new(44.4638, 26.0946);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    /// <summary>
    /// Runs <paramref name="work"/> inside a scope whose tenant context matches a real token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>KeycloakTenantContext</c> reads the tenant and the subject off the current request's
    /// principal and <b>throws</b> when there is no authenticated one — deliberately, so that a
    /// tenant-owned query can never run unscoped. That guard is what makes a plain
    /// <c>CreateScope()</c> useless here, and reaching around it with a stub would test a different
    /// tenant context from the one the server actually uses.
    /// </para>
    /// <para>
    /// So the principal is rebuilt from the fixture's own token: the claims are the ones the server
    /// would have seen had this arrived over HTTP, and every filter, interceptor and scope check runs
    /// exactly as it does in production. <c>IHttpContextAccessor</c> stores its context in an
    /// <c>AsyncLocal</c>, so setting it here reaches the scope's services and nothing outside this
    /// call.
    /// </para>
    /// </remarks>
    private async Task<T> AsAsync<T>(string token, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = fixture.Services.CreateScope();

        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;

        accessor.HttpContext = new DefaultHttpContext { User = PrincipalOf(token) };

        try
        {
            return await work(scope.ServiceProvider);
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }

    /// <summary>The claims inside a JWT, without validating it — the server already did that.</summary>
    private static ClaimsPrincipal PrincipalOf(string token)
    {
        var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var padded = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

        using var document = JsonDocument.Parse(Convert.FromBase64String(padded));

        // Only the two the tenant context reads. Permissions are not needed: the service is being
        // called directly, so no endpoint filter runs — and adding them would make this look like a
        // test of authorization, which it is not.
        var claims = new List<Claim>
        {
            new("tenant", document.RootElement.GetProperty("tenant").GetString()!),
            new("sub", document.RootElement.GetProperty("sub").GetString()!),
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static string SubjectOf(string token) =>
        PrincipalOf(token).FindFirst("sub")!.Value;

    private static async Task<Guid> OutletAsync(HttpClient client)
    {
        var channel = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var created = await client.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone, Location: Shop));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return (await created.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    /// <summary>Checks in over HTTP and returns the visit and its outlet.</summary>
    private async Task<(Guid VisitId, Guid OutletId)> VisitAsync(HttpClient client)
    {
        var outletId = await OutletAsync(client);

        var response = await client.PostAsJsonAsync(
            "/api/visits/check-in", new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var visit = (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        return (visit.Id, outletId);
    }

    private static CapturedAudit Audit(Guid visitId, Guid? auditId = null) => new(
        auditId ?? Guid.CreateVersion7(),
        visitId,
        new DateTimeOffset(2026, 4, 6, 9, 30, 0, TimeSpan.Zero),
        WeightSetVersion: 3,
        CategoryFacings: 40,
        Availability: [new CapturedAvailability(Guid.CreateVersion7(), AvailabilityStatus.OutOfStock)],
        Facings: [new CapturedFacings(Guid.CreateVersion7(), 6)],
        Prices: [new CapturedPrice(Guid.CreateVersion7(), 1099, 999, "RON")]);

    private Task<AuditIngestResult> IngestAsync(CapturedAudit audit, string? token = null) =>
        AsAsync(token ?? fixture.AdminAccessToken, services =>
            services.GetRequiredService<IAuditIngest>()
                .IngestAsync(audit, SubjectOf(token ?? fixture.AdminAccessToken)));

    [Fact]
    public async Task An_audit_pushed_against_an_open_visit_is_stored_and_readable()
    {
        using var client = Admin();

        var (visitId, outletId) = await VisitAsync(client);

        Assert.True((await IngestAsync(Audit(visitId))).Applied);

        var stored = await client.GetFromJsonAsync<AuditResponse>($"/api/visits/{visitId}/audit");

        Assert.NotNull(stored);
        Assert.Equal(visitId, stored.VisitId);

        // The outlet came from the visit, not the payload — `CapturedAudit` has no field for it, and
        // this is the assertion that keeps it that way.
        Assert.Equal(outletId, stored.OutletId);
        Assert.Equal(3, stored.WeightSetVersion);
        Assert.Equal(40, stored.CategoryFacings);
        Assert.Single(stored.Availability);
        Assert.Equal(nameof(AvailabilityStatus.OutOfStock), stored.Availability[0].Status);
        Assert.Equal(6, Assert.Single(stored.Facings).Facings);

        // Computed on the way out rather than stored, so it cannot disagree with the two amounts.
        Assert.Equal(100, Assert.Single(stored.Prices).DeltaMinorUnits);
    }

    [Fact]
    public async Task A_visit_that_is_not_this_reps_is_indistinguishable_from_one_that_does_not_exist()
    {
        /*
         * Both answer UnknownVisit. A device sends ids it read out of its own store and nothing
         * stops a modified client sending a different one, so the two cases must not be tellable
         * apart — otherwise this becomes a way to discover whose visits exist.
         */
        using var client = Admin();

        var (visitId, _) = await VisitAsync(client);

        var fabricated = await IngestAsync(Audit(Guid.CreateVersion7()));
        var somebodyElses = await IngestAsync(Audit(visitId), fixture.ReadOnlyAccessToken);

        Assert.Equal(AuditIngestRefusal.UnknownVisit, fabricated.Refusal);
        Assert.Equal(AuditIngestRefusal.UnknownVisit, somebodyElses.Refusal);
        Assert.Equal(fabricated.Reason, somebodyElses.Reason);
    }

    [Fact]
    public async Task A_visit_in_another_tenant_is_unknown_too()
    {
        // The isolation gate. Tenant B's rep cannot attach an audit to tenant A's visit, and gets
        // the same answer as for a visit nobody created.
        using var client = Admin();

        var (visitId, _) = await VisitAsync(client);

        var result = await IngestAsync(Audit(visitId), fixture.TenantBAccessToken);

        Assert.Equal(AuditIngestRefusal.UnknownVisit, result.Refusal);
    }

    [Fact]
    public async Task A_sealed_visit_refuses_a_new_audit()
    {
        // BR-AUD-6, and the direction matters: the visit was filed as done, so attaching a fresh
        // measurement to it would change a record that has already been counted.
        using var client = Admin();

        var (visitId, _) = await VisitAsync(client);

        var checkedOut = await client.PostAsJsonAsync(
            $"/api/visits/{visitId}/check-out", new CheckOutRequest(VisitOutcome.Productive));

        Assert.Equal(HttpStatusCode.OK, checkedOut.StatusCode);

        var result = await IngestAsync(Audit(visitId));

        Assert.Equal(AuditIngestRefusal.VisitSealed, result.Refusal);
    }

    [Fact]
    public async Task Pushing_the_same_audit_twice_is_success_the_second_time()
    {
        /*
         * Audit and Sync commit separately, so a mutation can land here and lose its ledger entry;
         * the device then retries with the same audit id. Refusing that retry would tell a device
         * "refused" forever about work that is done.
         */
        using var client = Admin();

        var (visitId, _) = await VisitAsync(client);
        var audit = Audit(visitId);

        Assert.True((await IngestAsync(audit)).Applied);
        Assert.True((await IngestAsync(audit)).Applied);

        // …and there is still one audit, not two.
        var stored = await client.GetFromJsonAsync<List<AuditResponse>>(
            $"/api/outlets/{(await client.GetFromJsonAsync<AuditResponse>($"/api/visits/{visitId}/audit"))!.OutletId}/audits");

        Assert.Single(stored!, candidate => candidate.VisitId == visitId);
    }

    [Fact]
    public async Task A_replay_still_succeeds_after_the_visit_has_been_sealed()
    {
        /*
         * The case the obvious ordering gets wrong, and the reason the replay check comes *before*
         * the seal check.
         *
         * A rep audits a shelf, checks out, and the phone drains both — audit first, then check-out.
         * If the audit's ledger entry is then lost, the retry arrives against a visit that is now
         * sealed. That retry is not a new audit; refusing it would strand work that is already
         * stored.
         */
        using var client = Admin();

        var (visitId, _) = await VisitAsync(client);
        var audit = Audit(visitId);

        Assert.True((await IngestAsync(audit)).Applied);

        await client.PostAsJsonAsync(
            $"/api/visits/{visitId}/check-out", new CheckOutRequest(VisitOutcome.Productive));

        Assert.True((await IngestAsync(audit)).Applied);
    }

    [Fact]
    public async Task A_second_different_audit_against_one_visit_is_refused()
    {
        // Not a replay — a different audit id. Two would leave "this shop's availability last
        // Tuesday" with two answers and no rule for choosing. Refused by name rather than surfacing
        // as a unique-index violation.
        using var client = Admin();

        var (visitId, _) = await VisitAsync(client);

        Assert.True((await IngestAsync(Audit(visitId))).Applied);

        var second = await IngestAsync(Audit(visitId));

        Assert.Equal(AuditIngestRefusal.AlreadyAudited, second.Refusal);
    }

    [Fact]
    public async Task The_aggregates_refusals_reach_the_device_by_name()
    {
        // The mapping from AuditRefusal to AuditIngestRefusal, asserted once rather than trusted:
        // a device branches on these, and a refusal that arrived as the wrong name would show the
        // rep the wrong message.
        using var client = Admin();

        var (visitId, _) = await VisitAsync(client);

        var empty = Audit(visitId) with { Availability = [], Facings = [], Prices = [] };
        var negative = Audit(visitId) with { CategoryFacings = -1 };

        Assert.Equal(AuditIngestRefusal.Empty, (await IngestAsync(empty)).Refusal);
        Assert.Equal(AuditIngestRefusal.NegativeCount, (await IngestAsync(negative)).Refusal);

        // …and nothing was stored by either attempt.
        var stored = await client.GetAsync($"/api/visits/{visitId}/audit");
        Assert.Equal(HttpStatusCode.NotFound, stored.StatusCode);
    }

    [Fact]
    public async Task An_outlets_audits_come_back_newest_first_by_when_they_were_measured()
    {
        /*
         * By CapturedAtUtc, not by when the server stored them. A day of offline audits drained at
         * once shares a CreatedAtUtc to the second, and ordering by that would put a shop's history
         * in whatever order the outbox happened to flush.
         *
         * The later-measured audit is deliberately stored *first*, which is what makes this test
         * able to fail: stored in capture order, the two orderings agree and the assertion proves
         * nothing. It passed against `OrderByDescending(CreatedAtUtc)` until this was swapped.
         */
        using var client = Admin();

        var outletId = await OutletAsync(client);

        var newer = await AuditedVisitAsync(client, outletId, new DateTimeOffset(2026, 4, 5, 9, 0, 0, TimeSpan.Zero));
        var older = await AuditedVisitAsync(client, outletId, new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero));

        var audits = await client.GetFromJsonAsync<List<AuditResponse>>($"/api/outlets/{outletId}/audits");

        var mine = audits!.Where(audit => audit.VisitId == older || audit.VisitId == newer).ToList();

        Assert.Equal([newer, older], mine.Select(audit => audit.VisitId));
    }

    [Fact]
    public async Task A_visit_with_no_audit_is_not_found()
    {
        using var client = Admin();

        var (visitId, _) = await VisitAsync(client);

        var response = await client.GetAsync($"/api/visits/{visitId}/audit");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_audit_belongs_to_its_tenant_and_no_other()
    {
        using var client = Admin();
        using var other = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var (visitId, _) = await VisitAsync(client);

        Assert.True((await IngestAsync(Audit(visitId))).Applied);

        var theirs = await other.GetAsync($"/api/visits/{visitId}/audit");

        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
    }

    [Fact]
    public async Task Reading_an_audit_needs_the_permission_that_governs_reading_a_visit()
    {
        /*
         * Not a permission of its own: an audit *is* what happened during a visit, and a supervisor
         * who may see where a rep checked in from is not a different person from one who may see
         * what they counted on the shelf.
         *
         * The `rep` fixture user is the right subject for this — a real, valid token in this tenant
         * that holds product and outlet reads and *not* `visit:read`. A 403 rather than a 401 is the
         * assertion: an unauthenticated caller would prove nothing about which permission is being
         * checked.
         */
        using var client = Admin();
        using var withoutVisitRead = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var (visitId, _) = await VisitAsync(client);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await withoutVisitRead.GetAsync($"/api/visits/{visitId}/audit")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await withoutVisitRead.GetAsync($"/api/outlets/{Guid.CreateVersion7()}/audits")).StatusCode);
    }

    /// <summary>Checks in at <paramref name="outletId"/>, audits it at a given moment, and returns the visit id.</summary>
    private async Task<Guid> AuditedVisitAsync(
        HttpClient client, Guid outletId, DateTimeOffset capturedAt)
    {
        var response = await client.PostAsJsonAsync(
            "/api/visits/check-in", new CheckInRequest(outletId, Shop.Latitude, Shop.Longitude));

        var visit = (await response.Content.ReadFromJsonAsync<VisitDetailResponse>())!.Visit;

        Assert.True((await IngestAsync(Audit(visit.Id) with { CapturedAtUtc = capturedAt })).Applied);

        // Sealed so the next check-in at this outlet is not refused for one already in progress.
        await client.PostAsJsonAsync(
            $"/api/visits/{visit.Id}/check-out", new CheckOutRequest(VisitOutcome.Productive));

        return visit.Id;
    }
}
