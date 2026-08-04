using FieldKit.BuildingBlocks;
using FieldKit.Modules.Catalog;
using FieldKit.Modules.Configuration;
using FieldKit.Modules.Iam;
using FieldKit.Modules.Org;
using FieldKit.Modules.Outlets;
using FieldKit.Server;
using FieldKit.SharedKernel;
using FieldKit.Web;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults (OpenTelemetry, health checks, resilience) + Redis output cache.
builder.AddServiceDefaults();
builder.AddRedisClientBuilder("cache").WithOutputCache();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Validate the Keycloak-issued JWT (ADR-0008) and reject one without a usable tenant claim, so
// every authenticated request is attributable before it reaches an endpoint.
builder.AddKeycloakAuthentication();

// Cross-cutting: the clock and the tenant context, now derived from the validated token (ADR-0008).
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, KeycloakTenantContext>();

// The modular monolith: the host composes modules; it does not know how they work (module boundaries §1).
// IAM first — it owns the tenant registry every other module's isolation ultimately rests on.
IReadOnlyList<IModule> modules = [new IamModule(), new ConfigurationModule(), new OrgModule(), new OutletsModule(), new CatalogModule()];
builder.Services.AddModules(builder.Configuration, modules);

var app = builder.Build();

app.UseExceptionHandler();

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
