using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FieldKit.Modules.Audit;
using FieldKit.Modules.Audit.Contracts;
using FieldKit.Modules.Configuration.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// The device telling the server a photograph arrived (<c>OFF-08</c>, <c>B5</c>) — W11 slice 13a.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the server needs telling at all.</b> It never sees the upload — that goes browser to
/// storage on a signature it minted and then forgot — so until this call exists, a reference to a
/// photograph still on a phone and a reference to one that is never coming are the same row. That was
/// the state W11 shipped in, and it is what makes <i>synced</i> and <i>uploaded</i> impossible to
/// tell apart.
/// </para>
/// <para>
/// <b>The audits here are written through the aggregate, not through a visit.</b> Checking in,
/// publishing a weighting and ingesting is <see cref="AuditIngestTests"/>'s subject; what this file
/// needs is a stored reference to confirm, and routing through machinery it is not testing would make
/// these fail for reasons that have nothing to do with photographs.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class PhotoConfirmTests(ServerFixture fixture)
{
    private static string SomeKey() => $"audits/{Guid.CreateVersion7()}/{Guid.CreateVersion7()}.jpg";

    private sealed record Outcome(int Confirmed, int Unknown);

    /// <summary>Files an audit carrying one photo reference, and answers with the stored key.</summary>
    /// <remarks>
    /// The key is prefixed with the tenant exactly as <c>presign</c> would, because that is the form
    /// the device confirms — and confirming the unprefixed form is a mistake this would otherwise
    /// hide.
    /// </remarks>
    private async Task<(Guid AuditId, Guid VisitId, string ObjectKey)> AuditWithPhotoAsync(
        string token, DateTimeOffset? capturedAt = null)
    {
        var visitId = Guid.CreateVersion7();
        var key = SomeKey();

        return await AsAsync(token, async services =>
        {
            var tenant = services.GetRequiredService<FieldKit.BuildingBlocks.ITenantContext>();
            var db = services.GetRequiredService<AuditDbContext>();

            var prefixed = $"{tenant.TenantId.Value}/{key}";

            var (audit, refusal) = Modules.Audit.Audit.Record(
                new CapturedAudit(
                    Guid.CreateVersion7(),
                    visitId,
                    capturedAt ?? services.GetRequiredService<FieldKit.SharedKernel.IClock>().UtcNow,
                    1, 40, [], [], [],
                    Photos: [new CapturedPhoto(AuditSection.ShareOfShelf, prefixed)]),
                Guid.CreateVersion7(),
                "rep-1",
                [new PillarWeight(ScorePillar.Availability, 100m)]);

            Assert.Equal(AuditRefusal.None, refusal);

            db.Audits.Add(audit!);
            await db.SaveChangesAsync();

            return (audit!.Id, visitId, prefixed);
        });
    }

    [Fact]
    public async Task Refuses_an_anonymous_caller()
    {
        // Which photographs a tenant is waiting on is that tenant's business.
        var response = await fixture.Client.PostAsJsonAsync(
            "/api/sync/photos/confirm", new { objectKeys = new[] { SomeKey() } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refuses_a_confirmation_that_names_nothing()
    {
        // An empty list is a device bug, not an upload — and answering "0 confirmed" to it would let
        // that bug run forever looking like success.
        var response = await fixture.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/sync/photos/confirm", new { objectKeys = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Marks_a_photograph_as_arrived()
    {
        var (_, visitId, key) = await AuditWithPhotoAsync(fixture.AccessToken);

        var response = await fixture.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/sync/photos/confirm", new { objectKeys = new[] { key } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new Outcome(1, 0), await response.Content.ReadFromJsonAsync<Outcome>());

        // Read back through the contract a supervisor's screen uses, not the column: what matters is
        // that the state a reader sees changed, and the state is derived rather than stored.
        var photo = await ReadPhotoAsync(fixture.AccessToken, visitId);

        Assert.Equal(PhotoEvidenceState.Arrived, photo.State);
        Assert.NotNull(photo.UploadedAtUtc);
    }

    [Fact]
    public async Task Confirming_twice_changes_nothing_the_second_time()
    {
        /*
         * The retry, which is ordinary rather than exceptional: a device that loses the answer asks
         * again, and it must not be punished or believed twice.
         *
         * The timestamp is the part worth asserting. If a repeat overwrote it, an audit's evidence
         * would appear to arrive later every time a rep drove through a tunnel — and the record of
         * *when* a photograph landed is the only thing this column is for.
         */
        var (_, visitId, key) = await AuditWithPhotoAsync(fixture.AccessToken);
        var client = fixture.CreateAuthenticatedClient();

        await client.PostAsJsonAsync("/api/sync/photos/confirm", new { objectKeys = new[] { key } });
        var first = (await ReadPhotoAsync(fixture.AccessToken, visitId)).UploadedAtUtc;

        var again = await client.PostAsJsonAsync(
            "/api/sync/photos/confirm", new { objectKeys = new[] { key } });

        Assert.Equal(new Outcome(0, 0), await again.Content.ReadFromJsonAsync<Outcome>());
        Assert.Equal(first, (await ReadPhotoAsync(fixture.AccessToken, visitId)).UploadedAtUtc);
    }

    [Fact]
    public async Task Counts_a_key_no_audit_claims_rather_than_refusing_it()
    {
        /*
         * <b>The case the whole split exists for.</b> The upload can beat the push, so a device can
         * legitimately confirm a photograph whose audit is still in its own outbox. Refusing that
         * would fail exactly the rep this design is meant to serve — and the count is what tells the
         * device to try again after the next push rather than dropping it.
         */
        var response = await fixture.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/sync/photos/confirm", new { objectKeys = new[] { SomeKey() } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new Outcome(0, 1), await response.Content.ReadFromJsonAsync<Outcome>());
    }

    [Fact]
    public async Task Does_not_let_one_tenant_confirm_another_tenants_photograph()
    {
        /*
         * A key is a string a device holds, and a modified client can send any string it likes. The
         * defence is not a check written here — it is that every query in Audit runs under the tenant
         * filter, so the other tenant's row is not there to be found.
         *
         * Asserted in three parts, because the first two alone would pass against a service that
         * confirmed nothing at all: the intruder is told the key is unknown, the owner's photograph
         * is still waiting afterwards, and then the **same key from the owner** confirms. Only the
         * tenant differed, so only the tenant can explain the refusal.
         *
         * This is also the one rule I could not sabotage to check: removing the filter means
         * `IgnoreQueryFilters`, and the architecture gate refuses to compile it (RS0030).
         */
        var (_, visitId, key) = await AuditWithPhotoAsync(fixture.AccessToken);

        var intruder = await fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken)
            .PostAsJsonAsync("/api/sync/photos/confirm", new { objectKeys = new[] { key } });

        Assert.Equal(new Outcome(0, 1), await intruder.Content.ReadFromJsonAsync<Outcome>());

        var photo = await ReadPhotoAsync(fixture.AccessToken, visitId);

        Assert.Equal(PhotoEvidenceState.Expected, photo.State);
        Assert.Null(photo.UploadedAtUtc);

        var owner = await fixture.CreateAuthenticatedClient()
            .PostAsJsonAsync("/api/sync/photos/confirm", new { objectKeys = new[] { key } });

        Assert.Equal(new Outcome(1, 0), await owner.Content.ReadFromJsonAsync<Outcome>());
    }

    [Fact]
    public async Task Confirms_the_photographs_it_knows_and_reports_the_rest()
    {
        // A device uploads serially and confirms in batches, so a mixed batch is the normal shape
        // once one audit has landed and the next has not.
        var (_, _, mine) = await AuditWithPhotoAsync(fixture.AccessToken);

        var response = await fixture.CreateAuthenticatedClient().PostAsJsonAsync(
            "/api/sync/photos/confirm", new { objectKeys = new[] { mine, SomeKey(), SomeKey() } });

        Assert.Equal(new Outcome(1, 2), await response.Content.ReadFromJsonAsync<Outcome>());
    }

    private Task<PhotoLine> ReadPhotoAsync(string token, Guid visitId) =>
        AsAsync(token, async services =>
        {
            var audits = services.GetRequiredService<IAuditQuery>();
            var record = await audits.ForVisitAsync(visitId);

            return record!.Photos.Single();
        });

    /// <summary>
    /// Runs <paramref name="work"/> under a tenant context built from a real token.
    /// </summary>
    /// <remarks>
    /// The same approach <see cref="AuditIngestTests"/> takes, and for the same reason: the tenant
    /// context reads the current request's principal and throws without one, so a tenant-owned query
    /// can never run unscoped. Reaching around it with a stub would test a different tenant context
    /// from the one that ships.
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

    private static ClaimsPrincipal PrincipalOf(string token)
    {
        var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var padded = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

        using var document = JsonDocument.Parse(Convert.FromBase64String(padded));

        return new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("tenant", document.RootElement.GetProperty("tenant").GetString()!),
                new Claim("sub", document.RootElement.GetProperty("sub").GetString()!),
            ],
            "test"));
    }
}
