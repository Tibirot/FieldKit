using System.Net;
using System.Net.Http.Json;
using System.Text;
using FieldKit.Modules.Sync;

namespace FieldKit.Server.Tests;

/// <summary>
/// The device registry: bind, one active per rep, and revoke (<c>OFF-12</c>, sync engine §7).
/// </summary>
/// <remarks>
/// <para>
/// The rule under test is exclusivity, and it is worth being precise about which half of it: binding
/// a new device deactivates the previous one so that <b>pull</b> has exactly one answer to "is this
/// the rep's device". Push deliberately does not care — a replaced device may still drain work it
/// captured before the swap, which is the difference between "one active device" and "lose a day's
/// visits" (A8). That asymmetry arrives with push in W8 slice 5; here the registry only has to
/// record which device is which, and why.
/// </para>
/// <para>
/// Every request goes through a real token, so "the device belongs to the subject in the token"
/// is exercised rather than asserted — the bind request has no user id in it at all.
/// </para>
/// </remarks>
[Collection(ServerCollection.Name)]
public class DeviceRegistryTests(ServerFixture fixture)
{
    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..16];

    private static async Task<DeviceResponse> BindAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/sync/devices", new BindDeviceRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DeviceResponse>())!;
    }

    private static async Task<IReadOnlyList<DeviceResponse>> MineAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<DeviceResponse>>("/api/sync/devices/mine"))!;

    [Fact]
    public async Task Binding_registers_the_device_to_the_caller()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var device = await BindAsync(client, Unique("Pixel"));

        Assert.True(device.IsActive);
        Assert.Null(device.DeactivatedBecause);
        Assert.NotEqual(default, device.BoundAtUtc);
    }

    [Fact]
    public async Task Binding_a_second_device_deactivates_the_first_as_swapped()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var first = await BindAsync(client, Unique("Old"));
        var second = await BindAsync(client, Unique("New"));

        var mine = await MineAsync(client);
        var previous = mine.Single(device => device.Id == first.Id);
        var current = mine.Single(device => device.Id == second.Id);

        Assert.False(previous.IsActive);
        // Swapped, not Compromised: the old phone keeps its right to one final drain-push, which is
        // what stops a day of offline work dying with a replaced handset.
        Assert.Equal(nameof(DeactivationReason.Swapped), previous.DeactivatedBecause);
        Assert.True(current.IsActive);
    }

    [Fact]
    public async Task A_rep_has_at_most_one_active_device_however_many_they_bind()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        await BindAsync(client, Unique("One"));
        await BindAsync(client, Unique("Two"));
        await BindAsync(client, Unique("Three"));

        var mine = await MineAsync(client);

        Assert.Single(mine.Where(device => device.IsActive));
    }

    [Fact]
    public async Task Two_binds_at_once_leave_one_active_device_and_a_refusal_rather_than_a_500()
    {
        /*
         * The race the unique index exists for, and the answer it used to give.
         *
         * `A_rep_has_at_most_one_active_device` above binds three times in sequence, which the
         * deactivate-then-insert path handles perfectly. Concurrently is a different story: both
         * requests read no active device, both insert one, and the loser hit
         * `UX_device_one_active_per_user` as an unhandled `DbUpdateException` — a 500 with a stack
         * trace, for a case the model has an opinion about.
         *
         * Found in the browser during W9 slice 1, where React's development double-invocation made
         * one component bind twice. The client de-duplicates in flight now, so this can no longer be
         * reached from FieldKit's own UI — which is exactly why it needs a test rather than a
         * reproduction: a second tab, a retried request, or any future caller can still race.
         *
         * What is asserted is the pair: **one** of the two succeeds, and the other is refused by name
         * rather than crashing. Which one wins is the database's business.
         */
        using var client = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        // Two in flight together. `WhenAll` on two already-started tasks is as close to simultaneous
        // as an integration test gets, and it reproduced the 500 reliably before the fix.
        var first = client.PostAsJsonAsync("/api/sync/devices", new BindDeviceRequest(Unique("RaceA")));
        var second = client.PostAsJsonAsync("/api/sync/devices", new BindDeviceRequest(Unique("RaceB")));

        var responses = await Task.WhenAll(first, second);

        // Whatever happened, nothing was a server error. That is the whole point.
        Assert.All(responses, response =>
            Assert.True(
                (int)response.StatusCode < 500,
                $"a racing bind answered {(int)response.StatusCode}: {response.Content.ReadAsStringAsync().Result}"));

        var created = responses.Count(response => response.StatusCode == HttpStatusCode.Created);
        var refused = responses.Count(response => response.StatusCode == HttpStatusCode.Conflict);

        // Either the two serialised cleanly — both created, the second swapping the first, which is
        // the ordinary path — or they collided and the loser was refused by name.
        Assert.Equal(2, created + refused);
        Assert.True(created >= 1, "neither bind succeeded");

        if (refused == 1)
        {
            var loser = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);

            Assert.Equal("device.bind.raced", Assert.Single(await Refusals.ProblemsOf(loser)).Code);
        }

        // The invariant the index is there to protect, whichever way the race went.
        Assert.Single((await MineAsync(client)).Where(device => device.IsActive));
    }

    [Fact]
    public async Task A_nameless_device_is_refused_with_a_code()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var response = await client.PostAsJsonAsync("/api/sync/devices", new BindDeviceRequest("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("device.bind.nameRequired", Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task Devices_are_listed_per_caller_not_per_tenant()
    {
        // Two subjects in the same tenant. `/mine` answering with both would leak one rep's device
        // inventory to another, and is the kind of thing a tenant filter alone does not catch.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var repDevice = await BindAsync(rep, Unique("RepPhone"));
        var adminDevice = await BindAsync(admin, Unique("AdminPhone"));

        var repsOwn = await MineAsync(rep);

        Assert.Contains(repsOwn, device => device.Id == repDevice.Id);
        Assert.DoesNotContain(repsOwn, device => device.Id == adminDevice.Id);
    }

    [Fact]
    public async Task Revoking_as_compromised_records_the_reason_that_blocks_a_drain()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var device = await BindAsync(rep, Unique("Lost"));

        var response = await admin.PostAsJsonAsync(
            $"/api/sync/devices/{device.Id}/revoke",
            new RevokeDeviceRequest(DeactivationReason.Compromised));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var revoked = (await response.Content.ReadFromJsonAsync<DeviceResponse>())!;

        Assert.False(revoked.IsActive);
        // The distinction the endpoint exists for: a stolen phone must not push fabricated visits,
        // where a swapped one may still drain (security §5).
        Assert.Equal(nameof(DeactivationReason.Compromised), revoked.DeactivatedBecause);
    }

    [Fact]
    public async Task A_revocation_reason_arrives_as_its_name()
    {
        // Raw JSON for the same reason `VisitWorkflowTests` needs it: posting `RevokeDeviceRequest`
        // serialises with the record's own converter, so the request and the assertion agree
        // whatever the wire format is. Sent as an administrator would send it — the name that comes
        // back in `deactivatedBecause` — this was a 400 until the converter was added, and only the
        // ordinal `2` worked.
        //
        // Worth its own test rather than folding into the one below, because the two say different
        // things: that one is about what revocation *does*, this one about whether an administrator
        // can express it at all.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var device = await BindAsync(rep, Unique("Stolen"));

        var response = await admin.PostAsync(
            $"/api/sync/devices/{device.Id}/revoke",
            new StringContent("""{"reason":"Compromised"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var revoked = (await response.Content.ReadFromJsonAsync<DeviceResponse>())!;
        Assert.Equal(nameof(DeactivationReason.Compromised), revoked.DeactivatedBecause);
    }

    [Fact]
    public async Task A_revocation_reason_that_is_not_one_is_refused()
    {
        // The other half, and it only means something next to the test above: with no converter
        // every string was refused, so this assertion would have passed against an endpoint that
        // accepted no reason a caller could name.
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var device = await BindAsync(rep, Unique("Muddled"));

        var response = await admin.PostAsync(
            $"/api/sync/devices/{device.Id}/revoke",
            new StringContent("""{"reason":"Misplaced"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Revoking_an_already_inactive_device_is_refused_with_a_code()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var first = await BindAsync(rep, Unique("Old"));
        await BindAsync(rep, Unique("New")); // deactivates the first

        var response = await admin.PostAsJsonAsync(
            $"/api/sync/devices/{first.Id}/revoke",
            new RevokeDeviceRequest(DeactivationReason.Compromised));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("device.revoke.alreadyInactive", Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task Revoking_needs_the_permission_that_binding_does_not()
    {
        using var rep = fixture.CreateAuthenticatedClient(fixture.AccessToken);

        var device = await BindAsync(rep, Unique("Mine"));

        // The rep bound this device without any permission, and cannot revoke it — revocation is
        // for a device its owner no longer holds, so it is an administrator's act by definition.
        var response = await rep.PostAsJsonAsync(
            $"/api/sync/devices/{device.Id}/revoke",
            new RevokeDeviceRequest(DeactivationReason.Compromised));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Binding_requires_a_token()
    {
        // fixture.Client carries no Authorization header. CreateAuthenticatedClient(null) would
        // have fallen back to the rep is token, which is how the first version of this test
        // "passed" an anonymous request with a perfectly good bearer on it.
        var response = await fixture.Client.PostAsJsonAsync(
            "/api/sync/devices", new BindDeviceRequest("Anything"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
