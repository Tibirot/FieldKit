using FieldKit.BuildingBlocks;
using FieldKit.Modules.Catalog;
using FieldKit.Server;
using FieldKit.SharedKernel;
using FieldKit.Web;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults (OpenTelemetry, health checks, resilience) + Redis output cache.
builder.AddServiceDefaults();
builder.AddRedisClientBuilder("cache").WithOutputCache();

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Validate the Keycloak-issued JWT (ADR-0008). Nothing requires it yet except /api/auth/whoami —
// the tenant context is still DevTenantContext until the next slice.
builder.AddKeycloakAuthentication();

// Cross-cutting: the clock and the (temporary) dev tenant context (ADR-0008 replaces this in Phase 1).
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, DevTenantContext>();

// The modular monolith: the host composes modules; it does not know how they work (module boundaries §1).
IReadOnlyList<IModule> modules = [new CatalogModule()];
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
