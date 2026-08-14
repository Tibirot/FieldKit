using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Journey;
using FieldKit.Modules.Org;
using FieldKit.Modules.Outlets;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FieldKit.Server.Tests;

/// <summary>
/// The dev seeding that gives a fresh environment somebody to be (W11).
/// </summary>
/// <remarks>
/// <para>
/// <b>These assert against the seeding the host actually ran</b>, not against a seeder constructed
/// for the occasion: the fixture boots the real <c>Program</c> with the real
/// <c>appsettings.Development.json</c>, so the rows below are there because startup put them there.
/// A test that instantiated <see cref="TenantSeeder"/> itself would pass with the hosted service
/// unregistered, which is the one failure worth catching.
/// </para>
/// <para>
/// <b>Why any of this exists.</b> Territory membership is what a device pull is scoped by
/// (<c>BR-ORG-3</c>), and membership hangs off a *user row* that nothing creates for a realm account.
/// So a rep signed in to a fresh environment saw an empty app — no shops, no round, no order screen
/// worth opening — and it took three API calls to make the field app reachable at all. Found during
/// W11's browser verification, after the wrong conclusion had already been drawn about realm roles.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class DevSeedingTests(ServerFixture fixture)
{
    /// <summary>The <c>rep</c> fixture's subject, pinned in the realm file so seeding can name it.</summary>
    private const string RepSubject = "00000000-0000-4000-8000-00000000a001";

    [Fact]
    public async Task The_dev_rep_has_a_user_record_without_anybody_creating_one()
    {
        /*
         * The row the field app cannot work without, and the one nothing in the product creates: a
         * realm account authenticates, but until a `User` exists there is nothing for a territory
         * assignment to hang off.
         */
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var users = await admin.GetFromJsonAsync<List<UserResponse>>("/api/iam/users");

        var rep = Assert.Single(users!, candidate => candidate.SubjectId == RepSubject);

        Assert.Equal("rep@fieldkit.local", rep.Email);
        Assert.True(rep.IsActive);

        // `BR-IAM-3` — a user holds at least one role, and the seed names it by template.
        Assert.NotEmpty(rep.RoleIds);
    }

    [Fact]
    public async Task The_subject_is_the_one_the_realm_issues_rather_than_one_the_seed_invented()
    {
        /*
         * The half that makes the rest possible, and the reason the realm files pin user ids.
         *
         * Keycloak generates a subject on import and the dev Keycloak deliberately has no data
         * volume, so before those ids were pinned every restart produced a *different* subject —
         * orphaning the previous run's rows and making a seed keyed on one impossible. This asserts
         * the two halves agree: the token the realm mints carries the id the config seeded.
         */
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var identity = await rep.GetFromJsonAsync<WhoAmIResponse>("/api/auth/whoami");

        Assert.Equal(RepSubject, identity!.Subject);
    }

    [Fact]
    public async Task Seeding_the_same_user_again_changes_nothing()
    {
        // Idempotent by subject. Startup runs on every boot, and a seeder that inserted each time
        // would give one realm account several user rows — and `subjectId` is what every visit,
        // order and assignment is attributed through.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var before = await admin.GetFromJsonAsync<List<UserResponse>>("/api/iam/users");

        await RunSeedersAsync();

        var after = await admin.GetFromJsonAsync<List<UserResponse>>("/api/iam/users");

        Assert.Equal(
            before!.Count(candidate => candidate.SubjectId == RepSubject),
            after!.Count(candidate => candidate.SubjectId == RepSubject));

        Assert.Equal(1, after.Count(candidate => candidate.SubjectId == RepSubject));
    }

    [Fact]
    public async Task A_territory_the_seed_names_but_this_database_does_not_have_is_skipped_quietly()
    {
        /*
         * The case a fresh database is always in. `Org:SeedRepAssignments` names *Bucharest North*,
         * which no test database has — so the seeder logs and moves on rather than inventing an
         * empty territory for the rep to cover, which would be a round of nowhere and an explanation
         * nobody could find.
         *
         * That the host booted at all is half the assertion; this is the other half.
         */
        Assert.Equal(
            0,
            await CountAsync("SELECT count(*)::int FROM org.territory WHERE \"Name\" = 'Bucharest North'"));
    }

    [Fact]
    public async Task A_territory_that_does_exist_gets_the_rep_on_it()
    {
        /*
         * The other half, and the one that would matter on a developer's machine: once the demo
         * territory exists, the next start puts the seeded rep on it — which is what turns a signed-in
         * rep into a rep with a round.
         *
         * Named exactly as the config does, because matching is by name: a human writes that file and
         * a name is what they know.
         */
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var orgUnit = await admin.PostAsJsonAsync(
            "/api/org/units", new { Name = $"Seeded-{Guid.NewGuid():N}"[..20] });

        Assert.Equal(HttpStatusCode.Created, orgUnit.StatusCode);

        var parentId = (await orgUnit.Content.ReadFromJsonAsync<OrgUnitResponse>())!.Id;

        var created = await admin.PostAsJsonAsync(
            "/api/org/territories", new { Name = "Bucharest North", OrgUnitId = parentId });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        /*
         * Counted on *this* territory, not on the rep.
         *
         * The first version asked "how many assignments does this subject have" and got 29 in a full
         * run and 1 alone — because half the suite gives the same `rep` fixture a territory to make
         * a journey or a pull work. Pinning the subject did not create that overlap, it made it
         * visible: before, the id was random per run but still shared by every test in it.
         */
        var territoryId = (await created.Content.ReadFromJsonAsync<TerritoryResponse>())!.Id;

        try
        {
            await RunSeedersAsync();

            Assert.Equal(1, await AssignmentsAsync(territoryId));

            // Open-ended: a development assignment that expired would fire weeks later as an empty
            // app on a Monday.
            Assert.Equal(
                1,
                await CountAsync(
                    $"""
                     SELECT count(*)::int FROM org.rep_assignment
                     WHERE "UserId" = '{RepSubject}' AND "TerritoryId" = '{territoryId}' AND to_date IS NULL
                     """));

            // …and running again does not add a second, which `BR-ORG-2` would refuse anyway.
            await RunSeedersAsync();

            Assert.Equal(1, await AssignmentsAsync(territoryId));
        }
        finally
        {
            // Removed so the *other* test in this file — the one asserting a fresh database has no
            // such territory — does not depend on which order xUnit picked.
            await ExecuteAsync(
                $"DELETE FROM org.rep_assignment WHERE \"TerritoryId\" = '{territoryId}'");
            await ExecuteAsync("DELETE FROM org.territory WHERE \"Name\" = 'Bucharest North'");
        }
    }

    [Fact]
    public async Task A_rep_with_no_shops_gets_no_round_rather_than_an_empty_one()
    {
        /*
         * The state every fresh database is in, and the one the host has to survive.
         *
         * `Journey:SeedRounds` names the same rep as the assignment seed, and this database has no
         * *Bucharest North* to put them on — so the rep covers nothing, and the seeder says so and
         * stops. A round of nowhere would be worse than no round: the screen would list calls at
         * shops the device cannot hold, which is the shape F1 spent a slice on.
         *
         * That the host booted at all is half the assertion. This is the other half.
         */
        Assert.Equal(0, await PlansAsync());
    }

    [Fact]
    public async Task A_rep_with_shops_gets_a_published_round_that_covers_today()
    {
        /*
         * <b>The item both regression sweeps asked for.</b> A rep with no published round cannot
         * reach check-in, the audit or order capture — so two manual passes got no further than the
         * empty-round message, and W12's own browser verification of F2b spent its first twenty
         * minutes generating and publishing one by hand.
         *
         * It goes through the **real generator**, which is why a frequency and a working week are
         * seeded rather than the calls: a seeder that wrote rows directly would leave a break in
         * `JRN-03` invisible to every browser pass, which is precisely the class of gap these two
         * sweeps kept finding.
         */
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var territoryId = await SeededTerritoryAsync(admin);

        try
        {
            await RunSeedersAsync();

            // The injected clock, not `DateTime.UtcNow` — which the analyser bans outright, and the
            // seeder reads the same one, so the two agree about what day it is even if a test ever
            // freezes it.
            var today = DateOnly.FromDateTime(Clock().UtcNow.UtcDateTime);

            // Published, not drafted: a device pulls published plans only, so a draft would leave
            // the rep exactly as empty-handed as no plan at all.
            Assert.Equal(1, await PlansAsync(published: true, covering: today));

            // …and it has calls on it. A plan with none is what a missing frequency produces, and it
            // looks like success from every angle except the rep's.
            Assert.True(await CallsAsync() > 0, "the seeded round has no calls on it");
        }
        finally
        {
            await CleanUpAsync(territoryId);
        }
    }

    [Fact]
    public async Task Starting_again_while_today_is_covered_builds_nothing()
    {
        /*
         * Idempotent by *coverage of today* rather than by row existence, which is the one place
         * this seeder departs from the two before it: a dev environment left running drifts out of
         * any fixed window, and a round that went stale three weeks ago is the same empty app.
         *
         * This asserts the half that must not regress — while today *is* covered, a restart is a
         * no-op. The other half, a lapsed window re-seeding, needs the clock moved and is named here
         * rather than left looking untested.
         */
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var territoryId = await SeededTerritoryAsync(admin);

        try
        {
            await RunSeedersAsync();
            var seeded = await PlansAsync();

            await RunSeedersAsync();

            Assert.Equal(seeded, await PlansAsync());
        }
        finally
        {
            await CleanUpAsync(territoryId);
        }
    }

    /// <summary>The territory the config names, with two shops on it for the generator to plan.</summary>
    private async Task<Guid> SeededTerritoryAsync(HttpClient admin)
    {
        var orgUnit = await admin.PostAsJsonAsync(
            "/api/org/units", new OrgUnitRequest($"Round-{Guid.NewGuid():N}"[..18]));

        var parentId = (await orgUnit.Content.ReadFromJsonAsync<OrgUnitResponse>())!.Id;

        var created = await admin.PostAsJsonAsync(
            "/api/org/territories", new TerritoryRequest("Bucharest North", parentId));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var territoryId = (await created.Content.ReadFromJsonAsync<TerritoryResponse>())!.Id;

        var channel = await admin.PostAsJsonAsync(
            "/api/outlets/channels", new ChannelRequest($"Round-{Guid.NewGuid():N}"[..18]));

        var channelId = (await channel.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;

        var outletIds = new List<Guid>();

        for (var index = 0; index < 2; index++)
        {
            var outlet = await admin.PostAsJsonAsync(
                "/api/outlets",
                new CreateOutletRequest(
                    $"RND-{Guid.NewGuid():N}"[..14], "Corner Shop", channelId, "Europe/Bucharest"));

            Assert.Equal(HttpStatusCode.Created, outlet.StatusCode);

            outletIds.Add((await outlet.Content.ReadFromJsonAsync<OutletResponse>())!.Id);
        }

        var assigned = await admin.PostAsJsonAsync(
            $"/api/org/territories/{territoryId}/outlets", new AssignOutletsRequest(outletIds));

        Assert.Equal(HttpStatusCode.NoContent, assigned.StatusCode);

        return territoryId;
    }

    /// <summary>
    /// Removes what the test built, so the *other* test in this file — the one asserting a fresh
    /// database seeds no round — does not depend on which order xUnit picked.
    /// </summary>
    private async Task CleanUpAsync(Guid territoryId)
    {
        await ExecuteAsync(
            $"""
             DELETE FROM journey.planned_visit WHERE "JourneyPlanId" IN
                 (SELECT "Id" FROM journey.journey_plan WHERE "UserId" = '{RepSubject}')
             """);

        await ExecuteAsync($"DELETE FROM journey.journey_plan WHERE \"UserId\" = '{RepSubject}'");
        await ExecuteAsync($"DELETE FROM journey.working_calendar WHERE \"UserId\" = '{RepSubject}'");
        await ExecuteAsync($"DELETE FROM org.rep_assignment WHERE \"TerritoryId\" = '{territoryId}'");
        await ExecuteAsync("DELETE FROM org.territory WHERE \"Name\" = 'Bucharest North'");
    }

    /// <summary>The seeded rep's plans, optionally only the published ones covering a day.</summary>
    private Task<int> PlansAsync(bool published = false, DateOnly? covering = null)
    {
        var filters = $"\"UserId\" = '{RepSubject}'";

        // The **name**, not an ordinal: `JourneyPlanStatus` is stored as a string, which the first
        // version of this got wrong and Postgres said so (`character varying = integer`). Spelled
        // out rather than read from the enum, because a test that asked the code under test what it
        // stores would agree with it whatever it stored.
        if (published) filters += " AND \"Status\" = 'Published'";

        if (covering is { } day)
        {
            filters += $" AND from_date <= '{day:yyyy-MM-dd}' AND to_date >= '{day:yyyy-MM-dd}'";
        }

        return CountAsync($"SELECT count(*)::int FROM journey.journey_plan WHERE {filters}");
    }

    private Task<int> CallsAsync() =>
        CountAsync(
            $"""
             SELECT count(*)::int FROM journey.planned_visit v
             JOIN journey.journey_plan p ON p."Id" = v."JourneyPlanId"
             WHERE p."UserId" = '{RepSubject}'
             """);

    /*
     * Raw SQL rather than the registered `OrgDbContext`, and not by preference.
     *
     * That context resolves its `ITenantContext` from the *request*, and refuses outside one — which
     * is the same refusal both seeders build their own context to work around, and it is correct:
     * a background reader quietly inheriting "some tenant" is how cross-tenant reads happen. A test
     * has no request either, so it asks the database directly. Test projects are exempt from the
     * raw-SQL ban for exactly this, per Directory.Build.props.
     */
    private async Task<int> CountAsync(string sql)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrgDbContext>();

        await using var command = db.Database.GetDbConnection().CreateCommand();

        await db.Database.OpenConnectionAsync();
        command.CommandText = sql;

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    /// <summary>The seeded rep's assignments, on one territory — see the note at the call site.</summary>
    private Task<int> AssignmentsAsync(Guid territoryId) =>
        CountAsync($"""
                    SELECT count(*)::int FROM org.rep_assignment
                    WHERE "UserId" = '{RepSubject}' AND "TerritoryId" = '{territoryId}'
                    """);

    /// <summary>The host’s own clock, which is what the seeder reads.</summary>
    private IClock Clock()
    {
        using var scope = fixture.Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<IClock>();
    }

    private async Task ExecuteAsync(string sql)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrgDbContext>();

        await using var command = db.Database.GetDbConnection().CreateCommand();

        await db.Database.OpenConnectionAsync();
        command.CommandText = sql;

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs the hosted seeders the host registered, as a restart would.</summary>
    private async Task RunSeedersAsync()
    {
        foreach (var seeder in fixture.Services.GetServices<IHostedService>())
        {
            if (seeder is TenantSeeder or RepAssignmentSeeder or JourneyRoundSeeder)
            {
                await seeder.StartAsync(CancellationToken.None);
            }
        }
    }
}
