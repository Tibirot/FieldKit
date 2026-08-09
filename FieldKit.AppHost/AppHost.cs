var builder = DistributedApplication.CreateBuilder(args);

// No Redis. It was here from W1 backing an output cache nothing ever used, and it left dev running
// a container the deploy would not have (ADR-0011 prices it as the largest avoidable line). It comes
// back in W8 with the sync idempotency ledger — a real consumer, and a different registration.

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
    .WithReference(database)
    .WaitFor(database)
    // The API validates tokens against this realm; wiring the reference now means service discovery
    // resolves the authority in the next slice rather than a hard-coded URL.
    .WithReference(keycloak)
    .WaitFor(keycloak)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// The Next.js app runs as its own process in development and as a container in production, calling
// the API through service discovery. It is a standalone app, not static files served by the Server
// (ADR-0004).
//
// `AddNextJsApp` rather than `AddJavaScriptApp`, and the difference is the whole of deploy slice D3.
// What `AddJavaScriptApp` published was not deployable:
//
//   - the resource had **no bindings at all** and `"buildOnly": true` — an image that gets built and
//     never run or exposed;
//   - the Dockerfile it generated had **no CMD**, so the container would start the base image's
//     `node` REPL and exit;
//   - that Dockerfile was single-stage and never touched `.next/standalone`, which is the entire
//     reason `output: "standalone"` is set in next.config.ts.
//
// `AddNextJsApp` gives an external http binding, injects PORT and HOSTNAME, validates at publish
// time that standalone output is configured, and generates a multi-stage Dockerfile that ships
// `.next/standalone` with `public/` and `.next/static` copied in, running as `node`. A Dockerfile
// was written by hand here first; it was deleted after diffing it against that one, which does the
// same job and does not have to be kept in step with next.config.ts by a human.
//
// Suppressed because `AddNextJsApp` is `[Experimental]`. That is a real cost — the API can change
// under an Aspire upgrade — and it is taken knowingly: the stable alternative cannot express a
// deployable Next.js app, and a breaking change here fails the build rather than the deployment.
#pragma warning disable ASPIREJAVASCRIPT001
var frontend = builder.AddNextJsApp("webfrontend", "../frontend")
    // No install step on start. `AddNextJsApp` otherwise attaches an installer resource that
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

// `NODE_ENV=production` in the published manifest.
//
// Aspire sets `development` for every JavaScript app, which is right for `dotnet run` and wrong for
// an image: the container would run the production build under a flag that says otherwise. Two
// things read it and neither is cosmetic — Next itself, and `proxy.ts`, which relaxes the Content
// Security Policy in development for the dev server's inline scripts. Left alone, the deployed app
// would serve the loosened policy, and the CSP is what the browser-held-token decision in ADR-0008
// rests on.
//
// Set after the chain above so it is the last word on the variable.
if (builder.ExecutionContext.IsPublishMode)
    frontend.WithEnvironment("NODE_ENV", "production");

// A fixed port in development, because a person types this one. `AddNextJsApp` assigns a random
// one, which is right for a service nothing addresses by hand and wrong for the app you open in a
// browser fifty times a day — it also silently invalidated `.claude/launch.json` and the toolchain
// doc, both of which name 3000. In publish mode the port comes from the host and is left alone.
if (builder.ExecutionContext.IsRunMode)
    frontend.WithEndpoint("http", endpoint => endpoint.Port = 3000);

#pragma warning restore ASPIREJAVASCRIPT001

builder.Build().Run();
