using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Org;
using FieldKit.Modules.Org.Contracts;
using FieldKit.Modules.Outlets;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// What a rep covers on a day (<c>ORG-04</c>), the contract journey generation is built on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exercised directly rather than through an endpoint</b>, because it has neither — <c>IRepScope</c>
/// is an in-process contract and its only caller is Journey, which does not exist yet. That makes
/// this file the whole of its coverage until slice 1, so the cases worth reading are the ones about
/// the <i>edges of the period</i>: an assignment is inclusive of its last day, and everything the
/// generator plans depends on that boundary being where the admin thinks it is.
/// </para>
/// <para>
/// Data is seeded over HTTP, as an admin, because that is the only path that enforces the rules a
/// realistic fixture needs — a rep assignment is refused unless the user resolves through
/// <c>IUserDirectory</c>, so a fabricated subject id would not produce the row this is about.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class RepScopeTests(ServerFixture fixture)
{
    private const string Zone = "Europe/Bucharest";

    private static readonly DateOnly From = new(2026, 3, 1);
    private static readonly DateOnly To = new(2026, 3, 31);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    /// <summary>
    /// Resolves the contract inside a scope that carries a tenant, the way a request would.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tenant comes from the validated token's <c>tenant</c> claim through
    /// <c>IHttpContextAccessor</c> — so a plain <c>CreateScope</c> has no tenant and the query filter
    /// refuses to run (asserted below). Rather than invent a second way to establish one, the
    /// principal is rebuilt from the very token the HTTP fixture authenticates with, so the contract
    /// answers for exactly the tenant that seeded the data.
    /// </para>
    /// <para>
    /// <b>This one keeps a wrapper where the other five lost theirs.</b> It is not a copy of
    /// <see cref="AsTenant"/> — it is a typed helper *over* it, resolving <c>IRepScope</c> so that
    /// eight call sites read as questions about the scope rather than about the container. What it
    /// no longer has is a second way of building the principal.
    /// </para>
    /// </remarks>
    private Task<T> AsTenantAsync<T>(string accessToken, Func<IRepScope, Task<T>> ask) =>
        AsTenant.RunAsync(
            fixture, accessToken, services => ask(services.GetRequiredService<IRepScope>()));

    private async Task<Guid> ChannelAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest(Unique("Channel")));

        return (await response.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    private async Task<Guid> OutletAsync(HttpClient client, Guid channelId)
    {
        var response = await client.PostAsJsonAsync(
            "/api/outlets", new CreateOutletRequest(Unique("OUT"), "Corner Shop", channelId, Zone));

        return (await response.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    /// <summary>A user this tenant has. Assignments resolve the subject through IAM, so it must be real.</summary>
    private async Task<string> RepAsync(HttpClient client)
    {
        var subjectId = Guid.NewGuid().ToString();
        var roles = await client.GetFromJsonAsync<List<RoleResponse>>("/api/iam/roles");

        var response = await client.PostAsJsonAsync("/api/iam/users", new
        {
            subjectId,
            email = $"{Guid.NewGuid():N}@fieldkit.local",
            displayName = "Fixture Rep",
            locale = "en-GB",
            timeZone = Zone,
            roleIds = new[] { roles!.First(role => role.IsSystemTemplate).Id },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return subjectId;
    }

    /// <summary>A territory holding <paramref name="outletIds"/>, under a fresh org unit.</summary>
    private async Task<Guid> TerritoryAsync(HttpClient client, params Guid[] outletIds)
    {
        var unit = await client.PostAsJsonAsync(
            "/api/org/units", new OrgUnitRequest(Unique("Unit")));
        var unitId = (await unit.Content.ReadFromJsonAsync<OrgUnitResponse>())!.Id;

        var territory = await client.PostAsJsonAsync(
            "/api/org/territories", new TerritoryRequest(Unique("Territory"), unitId));
        var territoryId = (await territory.Content.ReadFromJsonAsync<TerritoryResponse>())!.Id;

        if (outletIds.Length > 0)
        {
            var assigned = await client.PostAsJsonAsync(
                $"/api/org/territories/{territoryId}/outlets", new AssignOutletsRequest(outletIds));

            Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);
        }

        return territoryId;
    }

    private async Task AssignAsync(
        HttpClient client, Guid territoryId, string userId, DateOnly from, DateOnly? to)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/assignments",
            new RepAssignmentRequest(userId, from, to));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_rep_covers_the_outlets_of_every_territory_assigned_to_them()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var channelId = await ChannelAsync(client);
        var first = await OutletAsync(client, channelId);
        var second = await OutletAsync(client, channelId);
        var third = await OutletAsync(client, channelId);

        var north = await TerritoryAsync(client, first, second);
        var south = await TerritoryAsync(client, third);
        var rep = await RepAsync(client);

        await AssignAsync(client, north, rep, From, To);
        await AssignAsync(client, south, rep, From, To);

        var coverage = await AsTenantAsync(
            fixture.AdminAccessToken, scope => scope.ForRepAsync(rep, new DateOnly(2026, 3, 15)));

        // Flat and merged across territories: BR-ORG-1 gives an outlet one territory, so the union
        // cannot contain a duplicate and the generator gets a list it can plan straight from.
        Assert.Equal([first, second, third], coverage.OutletIds.Order());
        Assert.Equal([north, south], coverage.TerritoryIds.Order());
    }

    [Theory]
    [InlineData(2026, 3, 1)]
    [InlineData(2026, 3, 31)]
    public async Task The_first_and_last_days_of_an_assignment_are_covered(int year, int month, int day)
    {
        // Inclusive at both ends, and worth pinning: "until the 30th" means the 30th to whoever typed
        // it, and a plan that silently skipped that day would look like a generation bug rather than
        // an off-by-one in a boundary nobody tested.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(client, await ChannelAsync(client));
        var territoryId = await TerritoryAsync(client, outletId);
        var rep = await RepAsync(client);

        await AssignAsync(client, territoryId, rep, From, To);

        var coverage = await AsTenantAsync(
            fixture.AdminAccessToken, scope => scope.ForRepAsync(rep, new DateOnly(year, month, day)));

        Assert.Contains(outletId, coverage.OutletIds);
    }

    [Theory]
    [InlineData(2026, 2, 28)]
    [InlineData(2026, 4, 1)]
    public async Task A_day_outside_the_assignment_covers_nothing(int year, int month, int day)
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(client, await ChannelAsync(client));
        var territoryId = await TerritoryAsync(client, outletId);
        var rep = await RepAsync(client);

        await AssignAsync(client, territoryId, rep, From, To);

        var coverage = await AsTenantAsync(
            fixture.AdminAccessToken, scope => scope.ForRepAsync(rep, new DateOnly(year, month, day)));

        Assert.Empty(coverage.OutletIds);
        Assert.Empty(coverage.TerritoryIds);
    }

    [Fact]
    public async Task An_open_ended_assignment_covers_every_day_after_it_starts()
    {
        // "Until further notice" is the ordinary case — a rep covers a territory until somebody
        // decides otherwise — so a null end must not read as "ended".
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var outletId = await OutletAsync(client, await ChannelAsync(client));
        var territoryId = await TerritoryAsync(client, outletId);
        var rep = await RepAsync(client);

        await AssignAsync(client, territoryId, rep, From, to: null);

        var coverage = await AsTenantAsync(
            fixture.AdminAccessToken, scope => scope.ForRepAsync(rep, new DateOnly(2031, 12, 31)));

        Assert.Contains(outletId, coverage.OutletIds);
    }

    [Fact]
    public async Task A_rep_nobody_has_assigned_covers_nothing_rather_than_failing()
    {
        // An unassigned rep, a rep between assignments and a territory with no outlets in it are all
        // ordinary states. None of them is an error, and a caller should not have to tell them apart.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var rep = await RepAsync(client);

        var coverage = await AsTenantAsync(
            fixture.AdminAccessToken, scope => scope.ForRepAsync(rep, new DateOnly(2026, 3, 15)));

        Assert.Empty(coverage.TerritoryIds);
        Assert.Empty(coverage.OutletIds);
    }

    [Fact]
    public async Task An_assigned_territory_with_no_outlets_yet_covers_none_of_them()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var territoryId = await TerritoryAsync(client);
        var rep = await RepAsync(client);

        await AssignAsync(client, territoryId, rep, From, To);

        var coverage = await AsTenantAsync(
            fixture.AdminAccessToken, scope => scope.ForRepAsync(rep, new DateOnly(2026, 3, 15)));

        // The territory is still in scope — it is assigned, it simply holds nothing yet. Saying so
        // is the difference between "covers nowhere" and "covers a territory nobody has filled".
        Assert.Equal([territoryId], coverage.TerritoryIds);
        Assert.Empty(coverage.OutletIds);
    }

    [Fact]
    public async Task One_tenants_assignments_are_invisible_to_another()
    {
        // The isolation case that matters for a cross-module contract: the subject id is a Keycloak
        // string rather than a tenant-scoped id, so nothing about the *argument* says which tenant it
        // belongs to. Only the query filter does.
        using var tenantA = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var outletId = await OutletAsync(tenantA, await ChannelAsync(tenantA));
        var territoryId = await TerritoryAsync(tenantA, outletId);
        var rep = await RepAsync(tenantA);

        await AssignAsync(tenantA, territoryId, rep, From, To);

        var seenByA = await AsTenantAsync(
            fixture.AdminAccessToken, scope => scope.ForRepAsync(rep, new DateOnly(2026, 3, 15)));
        Assert.Contains(outletId, seenByA.OutletIds);

        var seenByB = await AsTenantAsync(
            fixture.TenantBAccessToken, scope => scope.ForRepAsync(rep, new DateOnly(2026, 3, 15)));

        Assert.Empty(seenByB.TerritoryIds);
        Assert.Empty(seenByB.OutletIds);
    }

    [Fact]
    public async Task The_contract_refuses_to_answer_outside_an_authenticated_request()
    {
        // The same guard ITerritoryDirectory has, and it earns its place for the same reason: this is
        // exactly the contract a background job would reach for, and without a tenant it would read
        // every tenant's assignments at once. It throws rather than quietly answering for nobody.
        using var scope = fixture.Services.CreateScope();
        var repScope = scope.ServiceProvider.GetRequiredService<IRepScope>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repScope.ForRepAsync("some-subject", new DateOnly(2026, 3, 15)));
    }

    [Fact]
    public async Task An_empty_user_id_is_answered_without_a_query()
    {
        // No tenant is established here on purpose: if this reached the database it would throw like
        // the test above, so answering proves the short-circuit rather than merely observing it.
        using var scope = fixture.Services.CreateScope();
        var repScope = scope.ServiceProvider.GetRequiredService<IRepScope>();

        var coverage = await repScope.ForRepAsync("  ", new DateOnly(2026, 3, 15));

        Assert.Empty(coverage.TerritoryIds);
        Assert.Empty(coverage.OutletIds);
    }
}
