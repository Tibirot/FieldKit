using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Org;
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
            if (seeder is TenantSeeder or RepAssignmentSeeder)
            {
                await seeder.StartAsync(CancellationToken.None);
            }
        }
    }
}
