using FieldKit.BuildingBlocks;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Org;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;
using FieldKit.Server;
using FieldKit.SharedKernel;
using FieldKit.Web;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults (OpenTelemetry, health checks, resilience) + Redis output cache.
builder.AddServiceDefaults();
builder.AddRedisClientBuilder("cache").WithOutputCache();

// Problem details that keep a 400 a 400: an unreadable body is the caller's mistake, and the plain
// UseExceptionHandler reported every one of them as a server fault (ProblemDetailsExtensions).
builder.AddRequestProblemDetails();
builder.Services.AddOpenApi();

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
IReadOnlyList<IModule> modules = [new IamModule(), new ConfigurationModule(), new OrgModule(), new OutletsModule(), new ProductsModule()];
builder.Services.AddModules(builder.Configuration, modules);

var app = builder.Build();

app.UseRequestExceptionHandler();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseOutputCache();

// Must precede the endpoints: authentication populates HttpContext.User, authorization enforces
// what individual endpoints ask for via RequireAuthorization().
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapModules(modules);

app.MapDefaultEndpoints();
app.UseFileServer();

app.Run();

// Exposed so the API integration tests can boot the real host (WebApplicationFactory<Program>).
public partial class Program;
