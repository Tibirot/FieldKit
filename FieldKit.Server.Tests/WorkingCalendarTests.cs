using System.Net;
using System.Net.Http.Json;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Journey;

namespace FieldKit.Server.Tests;

/// <summary>
/// The working calendar: a rep's pattern, the tenant's holidays, and the days that survive both
/// (<c>JRN-02</c>).
/// </summary>
/// <remarks>
/// The assertions worth reading are about <i>subtraction</i>. A pattern says which weekdays a rep
/// works and a holiday takes one of those days away, and the interesting failure is a plan that
/// sends somebody out on Christmas — which no test of either half on its own would catch.
/// </remarks>
[Collection(ServerCollection.Name)]
public class WorkingCalendarTests(ServerFixture fixture)
{
    /// <summary>
    /// A Monday of its own, per test.
    /// </summary>
    /// <remarks>
    /// <b>Holidays are tenant-wide, and this collection shares a database.</b> A holiday one test
    /// adds closes that date for every other test in the same tenant — which is correct behaviour
    /// and a broken fixture: three tests failed together, passed in isolation, and the difference
    /// was a Monday one of them had made into a bank holiday. Each test that touches a date takes a
    /// different week rather than every test cleaning up after itself, because cleanup that is
    /// forgotten once produces exactly this failure again, a month later, in a different test.
    /// </remarks>
    private static DateOnly MondayOfWeek(int week) => new DateOnly(2026, 3, 2).AddDays(7 * week);

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..18];

    /// <summary>A user this tenant has. A calendar names a rep through IAM, so it must be real.</summary>
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
            timeZone = "Europe/Bucharest",
            roleIds = new[] { roles!.First(role => role.IsSystemTemplate).Id },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return subjectId;
    }

    private static Task<HttpResponseMessage> SetCalendarAsync(
        HttpClient client, string userId, IEnumerable<DayOfWeek> days, int visitsPerDay) =>
        client.PutAsJsonAsync(
            $"/api/journey/calendars/{userId}",
            new WorkingCalendarRequest([.. days], visitsPerDay));

    private static async Task<List<WorkingDayResponse>> WorkingDaysAsync(
        HttpClient client, string userId, DateOnly from, DateOnly to) =>
        (await client.GetFromJsonAsync<List<WorkingDayResponse>>(
            $"/api/journey/calendars/{userId}/working-days?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}"))!;

    [Fact]
    public async Task A_reps_pattern_becomes_the_days_they_can_be_sent_out()
    {
        var weekStart = MondayOfWeek(0);

        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var rep = await RepAsync(client);

        var set = await SetCalendarAsync(client, rep, [DayOfWeek.Monday, DayOfWeek.Wednesday], 8);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        // One week: Monday the 2nd and Wednesday the 4th, and nothing else.
        var days = await WorkingDaysAsync(client, rep, weekStart, weekStart.AddDays(6));

        Assert.Equal(
            [weekStart, weekStart.AddDays(2)],
            days.Select(day => day.Date));

        Assert.All(days, day => Assert.Equal(8, day.Capacity));
    }

    [Fact]
    public async Task A_holiday_removes_a_day_the_rep_would_otherwise_have_worked()
    {
        var weekStart = MondayOfWeek(1);

        // The subtraction, and the whole reason both halves live in one slice. Neither the pattern
        // nor the holiday list is wrong on its own; a plan built from the pattern alone sends
        // somebody out on a closed day.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var rep = await RepAsync(client);
        await SetCalendarAsync(client, rep, [DayOfWeek.Monday, DayOfWeek.Wednesday], 8);

        var created = await client.PostAsJsonAsync(
            "/api/journey/holidays", new HolidayRequest(weekStart, Unique("Holiday")));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var days = await WorkingDaysAsync(client, rep, weekStart, weekStart.AddDays(6));

        // The Monday is gone entirely rather than present with no capacity — a day with zero calls
        // and a day nobody works are the same to a generator, and two shapes for one meaning is a
        // filter every caller has to remember.
        Assert.Equal([weekStart.AddDays(2)], days.Select(day => day.Date));
    }

    [Fact]
    public async Task A_holiday_on_a_day_the_rep_does_not_work_changes_nothing()
    {
        var weekStart = MondayOfWeek(2);

        // Subtracting something that was never there. Worth pinning because the obvious
        // implementation — remove holidays from the range, then apply the pattern — gets the same
        // answer, and the one after it might not.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var rep = await RepAsync(client);
        await SetCalendarAsync(client, rep, [DayOfWeek.Monday], 8);

        await client.PostAsJsonAsync(
            "/api/journey/holidays", new HolidayRequest(weekStart.AddDays(1), Unique("Holiday")));

        var days = await WorkingDaysAsync(client, rep, weekStart, weekStart.AddDays(6));

        Assert.Equal([weekStart], days.Select(day => day.Date));
    }

    [Fact]
    public async Task A_rep_with_no_calendar_has_no_working_days_rather_than_an_error()
    {
        var weekStart = MondayOfWeek(3);

        // "Unconfigured", the same answer FrequencyResolver gives for an outlet nobody has graded.
        // A 404 would have said "no such rep", which is a different and wrong thing.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var rep = await RepAsync(client);

        Assert.Empty(await WorkingDaysAsync(client, rep, weekStart, weekStart.AddDays(6)));
    }

    [Fact]
    public async Task Setting_a_calendar_twice_replaces_the_pattern_rather_than_adding_to_it()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var rep = await RepAsync(client);

        await SetCalendarAsync(client, rep, [DayOfWeek.Monday, DayOfWeek.Tuesday], 8);
        await SetCalendarAsync(client, rep, [DayOfWeek.Friday], 4);

        var stored = await client.GetFromJsonAsync<WorkingCalendarResponse>(
            $"/api/journey/calendars/{rep}");

        Assert.Equal(["Friday"], stored!.WorkingDays);
        Assert.Equal(4, stored.VisitsPerDay);
    }

    [Fact]
    public async Task Days_are_stored_deduplicated_and_in_order()
    {
        // A careless list is a caller being careless, not an admin describing something impossible —
        // so it is collapsed rather than refused. Sorted so two calendars holding the same days read
        // the same, which is what makes a screen diffable.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var rep = await RepAsync(client);

        await SetCalendarAsync(
            client, rep, [DayOfWeek.Wednesday, DayOfWeek.Monday, DayOfWeek.Wednesday], 6);

        var stored = await client.GetFromJsonAsync<WorkingCalendarResponse>(
            $"/api/journey/calendars/{rep}");

        Assert.Equal(["Monday", "Wednesday"], stored!.WorkingDays);
    }

    [Fact]
    public async Task A_rep_who_works_no_days_is_refused()
    {
        // Same shape as a zero call frequency: a calendar with no days and no calendar at all
        // produce the same empty plan, and the second already says it.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var rep = await RepAsync(client);
        var response = await SetCalendarAsync(client, rep, [], 8);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == "journey.calendar.noWorkingDays");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task A_capacity_that_is_not_a_days_work_is_refused(int visitsPerDay)
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var rep = await RepAsync(client);
        var response = await SetCalendarAsync(client, rep, [DayOfWeek.Monday], visitsPerDay);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("field").GetString() == "visitsPerDay");
    }

    [Fact]
    public async Task A_calendar_for_somebody_this_tenant_does_not_have_is_refused()
    {
        // The rep is IAM's. Nothing about a subject id says which tenant it belongs to, so without
        // this a typo becomes a calendar that no generation run will ever match to a person.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var response = await SetCalendarAsync(
            client, Guid.NewGuid().ToString(), [DayOfWeek.Monday], 8);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == "journey.calendar.unknownUser");
    }

    [Fact]
    public async Task The_same_holiday_twice_is_refused_rather_than_doubled()
    {
        var weekStart = MondayOfWeek(4);

        // A tenant importing a year twice should end up with one Christmas.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var date = weekStart.AddDays(21);

        var first = await client.PostAsJsonAsync(
            "/api/journey/holidays", new HolidayRequest(date, "National Day"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            "/api/journey/holidays", new HolidayRequest(date, "National Day (again)"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task A_period_that_ends_before_it_starts_is_refused()
    {
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var rep = await RepAsync(client);

        var response = await client.GetAsync(
            $"/api/journey/calendars/{rep}/working-days?from=2026-03-31&to=2026-03-01");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_period_nobody_should_ask_for_is_refused_rather_than_walked()
    {
        // This walks day by day, so an unbounded range is an unbounded loop reachable from an
        // endpoint. A cycle is at most a year, so a year and a bit is the widest honest ask.
        using var client = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);

        var rep = await RepAsync(client);

        var response = await client.GetAsync(
            $"/api/journey/calendars/{rep}/working-days?from=2026-01-01&to=2030-01-01");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problems = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        Assert.Contains(
            problems.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == "journey.calendar.periodTooLong");
    }

    [Fact]
    public async Task One_tenants_holidays_never_close_anothers_working_days()
    {
        var weekStart = MondayOfWeek(5);

        // Tenants are in different countries and keep different holidays; nothing but the tenant
        // filter stops one closing the other's working days.
        //
        // The rep belongs to tenant A and the holiday to tenant B, rather than the other way round,
        // because tenant B's realm user holds no `user:write` — it cannot create the profile a
        // calendar has to name. Same assertion, and it avoids widening a realm's permissions to suit
        // a test.
        using var tenantA = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var date = weekStart.AddDays(28);

        var repA = await RepAsync(tenantA);
        await SetCalendarAsync(tenantA, repA, [date.DayOfWeek], 8);

        var closedForB = await tenantB.PostAsJsonAsync(
            "/api/journey/holidays", new HolidayRequest(date, "B's holiday"));

        Assert.Equal(HttpStatusCode.Created, closedForB.StatusCode);

        var days = await WorkingDaysAsync(tenantA, repA, date, date);

        Assert.Equal([date], days.Select(day => day.Date));
    }

    [Fact]
    public async Task One_tenants_calendars_are_invisible_to_another()
    {
        var weekStart = MondayOfWeek(6);

        using var tenantA = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);

        var repA = await RepAsync(tenantA);
        await SetCalendarAsync(tenantA, repA, [DayOfWeek.Monday], 8);

        var seenByB = await tenantB.GetAsync($"/api/journey/calendars/{repA}");
        Assert.Equal(HttpStatusCode.NotFound, seenByB.StatusCode);

        // And asking for their days answers "unconfigured" rather than leaking the pattern.
        Assert.Empty(await WorkingDaysAsync(tenantB, repA, weekStart, weekStart.AddDays(6)));
    }

    [Fact]
    public async Task Offers_no_way_to_change_a_calendar_to_a_caller_who_may_only_read()
    {
        using var reader = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        var listed = await reader.GetAsync("/api/journey/calendars");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        var attempted = await reader.PutAsJsonAsync(
            $"/api/journey/calendars/{Guid.NewGuid()}",
            new WorkingCalendarRequest([DayOfWeek.Monday], 8));

        Assert.Equal(HttpStatusCode.Forbidden, attempted.StatusCode);
    }
}
