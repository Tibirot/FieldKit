var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");

// PostgreSQL — the system of record. One database; each module owns a schema (ADR-0005).
// A persistent data volume keeps dev data across runs; pgweb gives a quick admin UI in dev.
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithPgWeb();

var database = postgres.AddDatabase("fieldkitdb");

// Keycloak — the identity provider (ADR-0008). FieldKit never stores passwords.
//
// A tenant *is* a realm (realm-per-tenant), so the dev tenant is imported from source rather than
// clicked together by hand: see realms/README.md. Deliberately **no** data volume — Keycloak skips
// importing a realm that already exists, so a persisted volume would silently ignore edits to the
// realm file. Admin credentials are Aspire parameters in user-secrets, never in source.
var keycloak = builder.AddKeycloak("keycloak")
    .WithRealmImport("./realms");

var server = builder.AddProject<Projects.FieldKit_Server>("server")
    .WithReference(cache)
    .WaitFor(cache)
    .WithReference(database)
    .WaitFor(database)
    // The API validates tokens against this realm; wiring the reference now means service discovery
    // resolves the authority in the next slice rather than a hard-coded URL.
    .WithReference(keycloak)
    .WaitFor(keycloak)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// The Next.js app runs as its own process (Aspire assigns the port via PORT + generates a
// Dockerfile on publish), calling the API through service discovery. It is a standalone app, not
// static files served by the Server (ADR-0004).
builder.AddJavaScriptApp("webfrontend", "../frontend", "dev")
    // No install step on start. `AddJavaScriptApp` otherwise attaches an installer resource that
    // runs `npm install` before every run — and on Windows that rewrites `package-lock.json` even
    // when it has nothing to install (measured: 1.9s of no work, 23 insertions and 263 deletions),
    // which is exactly the lockfile CI's `npm ci` rejects. Running the app is not a reason to
    // modify a tracked file.
    //
    // `installCommand: "ci"` would also fix it and was measured at 46s per start, because `npm ci`
    // wipes node_modules unconditionally — too much to pay on every run. Installing dependencies
    // is a step you take when they change; see docs/engineering/frontend-toolchain.md.
    .WithNpm(install: false)
    .WithReference(server)
    .WaitFor(server)
    // The browser is redirected to Keycloak by *address*, so the front end needs the same one the
    // API resolves issuers against. Reaching one Keycloak by two addresses mints tokens whose
    // issuer the API has never heard of — a 401 that looks nothing like a configuration mistake.
    .WithReference(keycloak)
    .WaitFor(keycloak)
    .WithExternalHttpEndpoints();

builder.Build().Run();
