using System.Net;
using System.Net.Http.Json;
using FieldKit.Infrastructure.Outbox;
using FieldKit.Modules.Iam;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// Users administration (<c>IAM-03</c>), driven over HTTP through the real host.
/// </summary>
[Collection(ServerCollection.Name)]
public class UserAdministrationTests(ServerFixture fixture)
{
    private HttpClient Admin() => fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

    /// <summary>Creates a role to attach users to — BR-IAM-3 means a user cannot exist without one.</summary>
    private static async Task<Guid> NewRoleAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/iam/roles", new RoleRequest($"R{Guid.NewGuid():N}"[..10], ["product:read"]));
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<RoleResponse>())!.Id;
    }

    private static UserRequest NewUser(Guid roleId, string? locale = null, string? timeZone = null)
    {
        var unique = Guid.NewGuid().ToString("N")[..10];
        return new UserRequest(
            SubjectId: $"sub-{unique}",
            Email: $"{unique}@fieldkit.local",
            DisplayName: "Maria Ionescu",
            Locale: locale ?? "ro-RO",
            TimeZone: timeZone ?? "Europe/Bucharest",
            RoleIds: [roleId]);
    }

    [Fact]
    public async Task A_user_can_be_created_and_read_back()
    {
        using var client = Admin();
        var request = NewUser(await NewRoleAsync(client));

        var created = await client.PostAsJsonAsync("/api/iam/users", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var user = await created.Content.ReadFromJsonAsync<UserResponse>();
        Assert.NotNull(user);
        Assert.Equal(request.SubjectId, user!.SubjectId);
        Assert.Equal(request.RoleIds, user.RoleIds);
        Assert.True(user.IsActive);

        var fetched = await client.GetFromJsonAsync<UserResponse>($"/api/iam/users/{user.Id}");
        Assert.Equal(user.Id, fetched!.Id);
    }

    [Fact]
    public async Task A_user_must_hold_at_least_one_role()
    {
        // BR-IAM-3. A user with no roles is not a restricted user — they authenticate and can then
        // do nothing, which reads as a broken account rather than a disabled one.
        using var client = Admin();
        var request = NewUser(await NewRoleAsync(client)) with { RoleIds = [] };

        var response = await client.PostAsJsonAsync("/api/iam/users", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_role_that_does_not_exist_in_this_tenant_is_rejected()
    {
        using var client = Admin();
        var request = NewUser(await NewRoleAsync(client)) with { RoleIds = [Guid.NewGuid()] };

        var response = await client.PostAsJsonAsync("/api/iam/users", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-locale", "Europe/Bucharest")]
    [InlineData("ro-RO", "Mars/Olympus_Mons")]
    public async Task Locale_and_time_zone_are_validated_against_the_runtime(string locale, string timeZone)
    {
        // BR-IAM-5 makes both mandatory because every amount and timestamp renders through them.
        // Merely requiring a non-empty string would let an unknown zone through, and that fails at
        // render time in front of a rep instead of here in front of an admin who can fix it.
        using var client = Admin();
        var request = NewUser(await NewRoleAsync(client), locale, timeZone);

        var response = await client.PostAsJsonAsync("/api/iam/users", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_email_cannot_be_reused_within_a_tenant()
    {
        using var client = Admin();
        var first = NewUser(await NewRoleAsync(client));
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/iam/users", first)).StatusCode);

        var second = first with { SubjectId = $"sub-{Guid.NewGuid():N}"[..14] };
        var response = await client.PostAsJsonAsync("/api/iam/users", second);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Updating_a_profile_leaves_the_subject_link_alone()
    {
        // SubjectId is the link to the Keycloak account. Repointing it would silently reattribute
        // every visit, order and audit the user has ever recorded, so it is not editable — the API
        // accepts the field and the domain ignores it.
        using var client = Admin();
        var roleId = await NewRoleAsync(client);
        var created = await client.PostAsJsonAsync("/api/iam/users", NewUser(roleId));
        var user = await created.Content.ReadFromJsonAsync<UserResponse>();

        var updated = await client.PutAsJsonAsync(
            $"/api/iam/users/{user!.Id}",
            new UserRequest("sub-hijacked", user.Email, "Renamed", "en-GB", "Europe/London", [roleId]));

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var after = await updated.Content.ReadFromJsonAsync<UserResponse>();

        Assert.Equal(user.SubjectId, after!.SubjectId); // unchanged
        Assert.Equal("Renamed", after.DisplayName);     // the editable parts did change
        Assert.Equal("en-GB", after.Locale);
    }

    [Fact]
    public async Task Deactivating_a_user_publishes_UserDeactivated_to_the_outbox()
    {
        // The consequence that reaches beyond IAM: Sync releases the bound device (A8). Asserting it
        // over HTTP would only show the flag flipped; the event landing in the outbox is what makes
        // the rest of the platform find out, and it is invisible from the API surface.
        using var client = Admin();
        var created = await client.PostAsJsonAsync("/api/iam/users", NewUser(await NewRoleAsync(client)));
        var user = (await created.Content.ReadFromJsonAsync<UserResponse>())!;

        var response = await client.PostAsync($"/api/iam/users/{user.Id}/deactivate", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<UserResponse>())!.IsActive);

        Assert.Equal(1, await CountDeactivationsAsync(user.SubjectId));
    }

    [Fact]
    public async Task Deactivating_twice_does_not_publish_twice()
    {
        // Delivery is at-least-once, so a duplicate event is survivable — but publishing one per
        // click means an admin double-tapping produces work for every consumer downstream.
        using var client = Admin();
        var created = await client.PostAsJsonAsync("/api/iam/users", NewUser(await NewRoleAsync(client)));
        var user = (await created.Content.ReadFromJsonAsync<UserResponse>())!;

        await client.PostAsync($"/api/iam/users/{user.Id}/deactivate", null);
        var second = await client.PostAsync($"/api/iam/users/{user.Id}/deactivate", null);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, await CountDeactivationsAsync(user.SubjectId));
    }

    [Fact]
    public async Task User_administration_needs_its_own_permission()
    {
        // `rep` is authenticated and holds both product permissions — and cannot see users.
        using var repClient = fixture.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await repClient.GetAsync("/api/iam/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fixture.Client.GetAsync("/api/iam/users")).StatusCode);
    }

    /// <summary>
    /// Counts <c>UserDeactivated</c> messages naming this subject, straight from the outbox table.
    /// </summary>
    /// <remarks>
    /// <c>OutboxMessage</c> is not tenant-owned, so no query filter applies and this reads cleanly
    /// from a plain scope — no bypass needed, which is the point of keeping the exemption in the
    /// model rather than in a call site.
    /// </remarks>
    private async Task<int> CountDeactivationsAsync(string subjectId)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IamDbContext>();

        // Filter by Type in the database, by payload in memory: Content is `jsonb`, and Postgres has
        // no LIKE for it ("operator does not exist: jsonb ~~ jsonb"). Matching a subject id inside
        // the JSON is a test concern, not something worth a jsonb operator in the query.
        var payloads = await db.Set<OutboxMessage>()
            .Where(message => message.Type.Contains(nameof(FieldKit.Modules.Iam.Contracts.UserDeactivated)))
            .Select(message => message.Content)
            .ToListAsync();

        return payloads.Count(content => content.Contains(subjectId, StringComparison.Ordinal));
    }
}
