using System.Net;
using System.Net.Http.Json;
using FieldKit.Infrastructure.Outbox;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Org;
using FieldKit.Modules.Org.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// Rep–territory assignments over effective periods (<c>ORG-04</c>).
/// </summary>
[Collection(ServerCollection.Name)]
public class RepAssignmentTests(ServerFixture fixture)
{
    private static readonly DateOnly March = new(2026, 3, 1);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..20];

    private static DateOnly Day(int day) => new(2026, 3, day);

    private async Task<Guid> TerritoryAsync(HttpClient client)
    {
        var unit = await client.PostAsJsonAsync("/api/org/units", new OrgUnitRequest(Unique("Region"), null));
        var unitId = (await unit.Content.ReadFromJsonAsync<OrgUnitResponse>())!.Id;

        var response = await client.PostAsJsonAsync(
            "/api/org/territories", new TerritoryRequest(Unique("Territory"), unitId));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<TerritoryResponse>())!.Id;
    }

    private async Task<string> RepAsync(HttpClient client)
    {
        var subjectId = Guid.NewGuid().ToString();
        var roles = await client.GetFromJsonAsync<List<RoleResponse>>("/api/iam/roles");

        var response = await client.PostAsJsonAsync("/api/iam/users", new
        {
            subjectId,
            email = $"{Guid.NewGuid():N}@fieldkit.local",
            displayName = "Field Rep",
            locale = "ro-RO",
            timeZone = "Europe/Bucharest",
            roleIds = new[] { roles!.First(role => role.IsSystemTemplate).Id },
        });

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return subjectId;
    }

    private async Task<HttpResponseMessage> AssignAsync(
        HttpClient client, Guid territoryId, string userId, DateOnly from, DateOnly? to = null) =>
        await client.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/assignments", new RepAssignmentRequest(userId, from, to));

    [Fact]
    public async Task A_rep_is_assigned_for_a_period()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var territoryId = await TerritoryAsync(client);
        var rep = await RepAsync(client);

        var response = await AssignAsync(client, territoryId, rep, Day(1), Day(31));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<RepAssignmentResponse>();
        Assert.Equal(rep, created!.UserId);
        Assert.Equal(Day(1), created.From);
        Assert.Equal(Day(31), created.To);
        Assert.Equal("Field Rep", created.DisplayName);
    }

    [Fact]
    public async Task An_open_ended_assignment_needs_no_end_date()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var territoryId = await TerritoryAsync(client);
        var rep = await RepAsync(client);

        var response = await AssignAsync(client, territoryId, rep, Day(1));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.Null((await response.Content.ReadFromJsonAsync<RepAssignmentResponse>())!.To);
    }

    [Fact]
    public async Task Overlapping_assignments_are_rejected_and_adjacent_ones_are_not()
    {
        // BR-ORG-2. The handover case — one ends the 20th, the next starts the 21st — must be
        // allowed, and sharing a single day must not be: two reps covering one territory on one day
        // is exactly what the rule exists to prevent.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var territoryId = await TerritoryAsync(client);
        var first = await RepAsync(client);
        var second = await RepAsync(client);

        Assert.Equal(HttpStatusCode.Created, (await AssignAsync(client, territoryId, first, Day(10), Day(20))).StatusCode);

        // Shares only the 20th.
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await AssignAsync(client, territoryId, second, Day(20), Day(25))).StatusCode);

        // Entirely inside.
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await AssignAsync(client, territoryId, second, Day(12), Day(15))).StatusCode);

        // Starts the day after — the handover.
        Assert.Equal(
            HttpStatusCode.Created,
            (await AssignAsync(client, territoryId, second, Day(21), Day(25))).StatusCode);
    }

    [Fact]
    public async Task An_open_ended_assignment_blocks_everything_after_it_starts()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var territoryId = await TerritoryAsync(client);
        var first = await RepAsync(client);
        var second = await RepAsync(client);

        Assert.Equal(HttpStatusCode.Created, (await AssignAsync(client, territoryId, first, Day(10))).StatusCode);

        // A year later still clashes, because the first has no end.
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await AssignAsync(client, territoryId, second, new DateOnly(2027, 6, 1), null)).StatusCode);

        // …but before it starts is fine, provided it also finishes before.
        Assert.Equal(
            HttpStatusCode.Created,
            (await AssignAsync(client, territoryId, second, Day(1), Day(9))).StatusCode);
    }

    [Fact]
    public async Task An_assignment_cannot_end_before_it_starts()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var territoryId = await TerritoryAsync(client);
        var rep = await RepAsync(client);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await AssignAsync(client, territoryId, rep, Day(20), Day(10))).StatusCode);
    }

    [Fact]
    public async Task An_assignment_is_editable_and_does_not_clash_with_itself()
    {
        // Fully editable, so correcting a mistyped start date is one call. The subtlety worth a test:
        // the overlap check must exclude the row being edited, or every edit would conflict with the
        // assignment it is editing.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var territoryId = await TerritoryAsync(client);
        var rep = await RepAsync(client);
        var replacement = await RepAsync(client);

        var created = await (await AssignAsync(client, territoryId, rep, Day(10), Day(20)))
            .Content.ReadFromJsonAsync<RepAssignmentResponse>();

        var edited = await client.PutAsJsonAsync(
            $"/api/org/assignments/{created!.Id}", new RepAssignmentRequest(replacement, Day(11), Day(19)));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var after = await edited.Content.ReadFromJsonAsync<RepAssignmentResponse>();
        Assert.Equal(replacement, after!.UserId);
        Assert.Equal(Day(11), after.From);
    }

    [Fact]
    public async Task An_assignment_must_name_an_active_rep_this_tenant_has()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var territoryId = await TerritoryAsync(client);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await AssignAsync(client, territoryId, Guid.NewGuid().ToString(), Day(1))).StatusCode);

        // A deactivated user resolves through IUserDirectory — their past work keeps its author —
        // but assigning one is refused: an assignment says who *will* be covering the territory.
        var rep = await RepAsync(client);
        var users = await client.GetFromJsonAsync<List<UserResponse>>("/api/iam/users");
        var profileId = users!.Single(user => user.SubjectId == rep).Id;

        await client.PostAsync($"/api/iam/users/{profileId}/deactivate", null);

        Assert.Equal(
            HttpStatusCode.BadRequest, (await AssignAsync(client, territoryId, rep, Day(1))).StatusCode);
    }

    [Fact]
    public async Task Every_change_reaches_the_outbox()
    {
        // The event Sync and Journey react to. Asserted at the outbox because that is the guarantee —
        // published in the same transaction as the change, so a device cannot be re-scoped for a
        // change that was rolled back.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var territoryId = await TerritoryAsync(client);
        var first = await RepAsync(client);
        var second = await RepAsync(client);

        var created = await (await AssignAsync(client, territoryId, first, Day(10), Day(20)))
            .Content.ReadFromJsonAsync<RepAssignmentResponse>();

        await client.PutAsJsonAsync(
            $"/api/org/assignments/{created!.Id}", new RepAssignmentRequest(second, Day(10), Day(20)));

        await client.DeleteAsync($"/api/org/assignments/{created.Id}");

        var events = await OutboxEventsAsync(territoryId);

        // Create, hand over, remove — and the hand-over names both sides, which is what lets a
        // consumer shrink one device and grow another without having watched.
        Assert.Equal(3, events.Count);

        Assert.Equal(first, events[0].IncomingUserId);
        Assert.Null(events[0].OutgoingUserId);

        Assert.Equal(second, events[1].IncomingUserId);
        Assert.Equal(first, events[1].OutgoingUserId);

        Assert.Null(events[2].IncomingUserId);
        Assert.Equal(second, events[2].OutgoingUserId);
    }

    [Fact]
    public async Task Territories_and_reps_can_each_be_asked_what_they_cover()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var territoryId = await TerritoryAsync(client);
        var rep = await RepAsync(client);

        await AssignAsync(client, territoryId, rep, Day(1), Day(31));

        var byTerritory = await client.GetFromJsonAsync<List<RepAssignmentResponse>>(
            $"/api/org/territories/{territoryId}/assignments");
        var byRep = await client.GetFromJsonAsync<List<RepAssignmentResponse>>(
            $"/api/org/users/{rep}/assignments");

        Assert.Equal(rep, Assert.Single(byTerritory!).UserId);
        Assert.Equal(territoryId, Assert.Single(byRep!).TerritoryId);
    }

    [Fact]
    public async Task Whether_an_assignment_is_current_is_decided_against_today()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var territoryId = await TerritoryAsync(client);
        var rep = await RepAsync(client);
        var other = await RepAsync(client);

        // The host's clock, not a static one — the banned-API gate is right that they can differ,
        // and this is the same instant the endpoint will compare against. `admin` has no FieldKit
        // profile, so the endpoint falls back to UTC, which is what this matches.
        var today = DateOnly.FromDateTime(
            fixture.Services.GetRequiredService<FieldKit.SharedKernel.IClock>().UtcNow.UtcDateTime);

        await AssignAsync(client, territoryId, rep, today.AddDays(-1), today.AddDays(1));
        await AssignAsync(client, territoryId, other, today.AddDays(10), today.AddDays(20));

        var assignments = await client.GetFromJsonAsync<List<RepAssignmentResponse>>(
            $"/api/org/territories/{territoryId}/assignments");

        Assert.True(Assert.Single(assignments!, a => a.UserId == rep).IsCurrent);
        Assert.False(Assert.Single(assignments!, a => a.UserId == other).IsCurrent);
    }

    [Fact]
    public async Task One_tenants_assignments_are_invisible_to_another()
    {
        using var tenantA = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var territoryId = await TerritoryAsync(tenantA);
        var rep = await RepAsync(tenantA);

        var mine = await (await AssignAsync(tenantA, territoryId, rep, Day(1), Day(31)))
            .Content.ReadFromJsonAsync<RepAssignmentResponse>();

        // `rep-b` holds territory:write, so this is 404 from the query filter rather than 403 from
        // the permission check — otherwise the assertion proves nothing.
        var edit = await tenantB.PutAsJsonAsync(
            $"/api/org/assignments/{mine!.Id}", new RepAssignmentRequest(rep, Day(1), Day(31)));

        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);

        var visible = await tenantB.GetFromJsonAsync<List<RepAssignmentResponse>>(
            $"/api/org/users/{rep}/assignments");

        Assert.Empty(visible!);
    }

    /// <summary>
    /// The events this territory produced, oldest first, read straight from the outbox table.
    /// </summary>
    private async Task<IReadOnlyList<RepAssignmentChanged>> OutboxEventsAsync(Guid territoryId)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrgDbContext>();

        // Filtered by type in SQL and matched on the payload in memory: the content column is jsonb,
        // and a `Contains` against it translates to `jsonb ~~ jsonb`, which Postgres has no operator
        // for. Learned the hard way in the outbox tests for Catalog.
        //
        // `Type` holds the *assembly-qualified* name, so this matches on the type name rather than
        // comparing to `FullName` — which silently returns nothing, as it did on the first run.
        var messages = await db.Set<OutboxMessage>()
            .Where(message => message.Type.Contains(nameof(RepAssignmentChanged)))
            .OrderBy(message => message.OccurredOnUtc)
            .Select(message => message.Content)
            .ToListAsync();

        return
        [
            .. messages
                .Select(content => System.Text.Json.JsonSerializer.Deserialize<RepAssignmentChanged>(content)!)
                .Where(@event => @event.TerritoryId == territoryId),
        ];
    }
}
