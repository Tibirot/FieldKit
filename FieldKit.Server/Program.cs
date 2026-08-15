using System.Text.Json.Serialization;
using FieldKit.BuildingBlocks;
using FieldKit.Modules.Audit;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Journey;
using FieldKit.Modules.Order;
using FieldKit.Modules.Org;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;
using FieldKit.Modules.Sync;
using FieldKit.Modules.Visit;
using FieldKit.Server;
using FieldKit.SharedKernel;
using FieldKit.Web;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults: OpenTelemetry, health checks, resilience.
//
// No output cache, and its absence is deliberate — see the note where `UseOutputCache` used to be.
builder.AddServiceDefaults();

// The dependency checks behind /health: Postgres, Keycloak, and the outbox dispatchers
// (W13 slice 5). The template ships one self check tagged live and nothing else, so readiness and
// liveness answered the same question and an instance that had lost its database reported ready.
builder.AddFieldKitHealthChecks();

// Problem details that keep a 400 a 400: an unreadable body is the caller's mistake, and the plain
// UseExceptionHandler reported every one of them as a server fault (ProblemDetailsExtensions).
builder.AddRequestProblemDetails();
builder.Services.AddOpenApi();

/*
 * Blob storage for shelf photographs (`OFF-08`, `B5`, W11 slice 12a).
 *
 * <b>Registered only when the AppHost supplied one.</b> Aspire's `WithReference(photos)` writes the
 * `photos` connection string; a host booted without it — the test fixture, or a deployment that has
 * not been given storage — must still start, and `SyncModule` leaves `IPhotoStorage` unregistered so
 * the presign endpoint can say so honestly rather than the whole API failing to boot.
 *
 * The client resolves its credential from the connection string: an account key in development
 * (Azurite), a managed identity when published. `BlobPhotoStorage` signs differently for each.
 */
if (builder.Configuration.GetConnectionString("photos") is not null)
{
    builder.AddAzureBlobServiceClient("photos");
}

// Validate the Keycloak-issued JWT (ADR-0008) and reject one without a usable tenant claim, so
// every authenticated request is attributable before it reaches an endpoint.
builder.AddKeycloakAuthentication();

// Cross-cutting: the clock and the tenant context, now derived from the validated token (ADR-0008).
// Money leaves this API as { "amount": "12.50", "currency": "EUR" } — a *string* amount, because
// JavaScript has no decimal type and a JSON number becomes a float the moment a browser parses it
// (BR-PRD-8, api-contracts §1). Registered here rather than attributed at each DTO: forgetting an
// attribute would emit a float silently, in the one part of the system with a business rule against
// them, and it would look correct in any test that round-trips through a typed client.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new MoneyJsonConverter());

    /*
     * Every enum crosses this API as its **name**, and the argument is the one directly above.
     *
     * It was already the intended rule — `api-contracts §1` states it, and twenty-two properties
     * across eight modules said so one `[JsonConverter]` at a time. An attribute per property is a
     * rule enforced by whoever remembers it, and the failure when someone does not is silent in
     * exactly the wrong way: `"status": 0` is valid JSON that a client will happily parse, so the
     * mistake surfaces as a screen rendering the wrong word rather than as an error. The visit
     * workflow's step type shipped that way until somebody posted `"Audit"` and got a 400.
     *
     * The generic form cannot describe a *collection* of enums, which is why Journey had to register
     * a whole converter for `DayOfWeek` — an attribute there throws at first use. This subsumes that
     * registration and every attribute alongside it.
     */
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());

    // A non-nullable property that the caller simply left out is a *bad request*, not a null.
    //
    // Without this, `{"name":"Supervisor"}` to an endpoint whose record declares
    // `IReadOnlyList<Guid> Permissions` deserializes to null, and the handler's first
    // `request.Permissions.Where(...)` is a NullReferenceException — a 500 blaming the server for a
    // field the caller omitted. Nine endpoints across IAM and Products were reachable that way; a
    // pre-W7 sweep found them by PUTting `{}`.
    //
    // Set here rather than guarded in each handler because the nullable annotation is already the
    // declaration — every request record says which parts are optional by writing `?`. Repeating
    // that as a null check per field would be a second copy of the same statement, and the copy
    // that gets forgotten is the one nobody reads.
    //
    // Both flags are needed and they cover different mistakes, which is easy to get wrong: the
    // first refuses an *explicit* `"permissions": null`, the second refuses `permissions` being
    // *absent*. Only the pair turns every shape of "the caller left it out" into a 400 — setting
    // one alone still 500s on the other, which is how the first attempt at this fix failed.
    options.SerializerOptions.RespectNullableAnnotations = true;
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
});

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, KeycloakTenantContext>();

// The modular monolith: the host composes modules; it does not know how they work (module boundaries §1).
// IAM first — it owns the tenant registry every other module's isolation ultimately rests on.
// Journey last: it reads outlets through Outlets' contracts, and the order here is the order the
// registry lists them in — dependencies before the modules that consume them.
IReadOnlyList<IModule> modules =
[
    new IamModule(),
    new ConfigurationModule(),
    new OrgModule(),
    new OutletsModule(),
    new ProductsModule(),
    new JourneyModule(),
    new VisitModule(),

    // Audit after Visit, because it reads Visit's contracts and nothing reads its own.
    new AuditModule(),

    // Order beside Audit, for the same reason and with the same shape: both read Visit's contracts,
    // both are written only through `/sync/push`, and nothing reads either of them yet. Sync will,
    // in W11 slice 5.
    new OrderModule(),

    // Sync last. It is the module that will eventually read every other one's change feed, so it is
    // the one whose dependencies point at the rest rather than the other way round.
    new SyncModule(),
];
builder.Services.AddModules(builder.Configuration, modules);

var app = builder.Build();

app.UseRequestExceptionHandler();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

/*
 * There is no output cache here, and that is a decision rather than an omission.
 *
 * One was wired in W1 — a Redis-backed `UseOutputCache()` — and **nothing ever opted in**: not one
 * endpoint called `.CacheOutput()` across seven weeks of building them. It was middleware with no
 * work to do and a Redis dependency behind it, which the deploy costing (ADR-0011) priced at more
 * than everything else on the bill put together.
 *
 * It is also the wrong default to leave lying around in *this* API. Every read here is tenant-scoped
 * and permission-gated; an output cache keyed on the URL would serve one tenant's rows to the next
 * caller, and the cache key policy that avoids it (vary by tenant *and* by the caller's permissions)
 * is a design decision nobody has made. A future `.CacheOutput()` would have inherited that hazard
 * silently — in a codebase whose central rule is that a tenant never sees another's data.
 *
 * When something genuinely needs caching, it arrives with its key policy and its own test.
 *
 * This used to add that Redis would return in W8 for the sync idempotency ledger. It does not: that
 * ledger is a Postgres table, decided at the start of W8 on cost grounds (ADR-0007 amendment). There
 * is no Redis in this system.
 */

// Must precede the endpoints: authentication populates HttpContext.User, authorization enforces
// what individual endpoints ask for via RequireAuthorization().
app.UseAuthentication();
app.UseAuthorization();

// After both, because the tenant comes from a token that has been validated and from nowhere else
// (W13 slice 2). Stamping the request's own span means "everything this tenant did" is a filter
// rather than a join, and it adds no span of its own.
app.UseTenantTracing();

app.MapAuthEndpoints();
app.MapModules(modules);

// Reporting is a composition rather than a module: no schema, no writes, and four contracts read
// side by side. It is mapped here for the same reason it lives here — see `ReportingEndpoints`.
app.MapReportingEndpoints();

app.MapDefaultEndpoints();
app.UseFileServer();

app.Run();

// Exposed so the API integration tests can boot the real host (WebApplicationFactory<Program>).
public partial class Program;
