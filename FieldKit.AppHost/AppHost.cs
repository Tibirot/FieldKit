var builder = DistributedApplication.CreateBuilder(args);

// No Redis. It was here from W1 backing an output cache nothing ever used, and it left dev running
// a container the deploy would not have (ADR-0011 prices it as the largest avoidable line). It comes
// back in W8 with the sync idempotency ledger — a real consumer, and a different registration.

// The Azure Container Apps environment everything below is published into (ADR-0011).
//
// Declared rather than left to `azd` to invent, because the environment is where the cost decisions
// live — Log Analytics, and the scale rules set per app further down.
builder.AddAzureContainerAppEnvironment("fieldkit-env");

// PostgreSQL — the system of record. One database; each module owns a schema (ADR-0005).
// A persistent data volume keeps dev data across runs; pgweb gives a quick admin UI in dev.
//
// **Still a container when published, and ADR-0011 says it should be managed.** That switch —
// `AddAzurePostgresFlexibleServer(…).RunAsContainer(…)` — is a slice of its own rather than a line
// here: the Azure resource exposes neither `PrimaryEndpoint` nor the username/password parameters
// the Keycloak wiring below reads, so it needs explicit credential parameters, and those change how
// `dotnet run` bootstraps for every developer. Tried in this branch, reverted, and left as the next
// deploy slice. **Nothing should be deployed until it lands**: published as a container, Postgres
// has no point-in-time restore, which is the reason ADR-0011 chose managed at all.
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithPgWeb();

var database = postgres.AddDatabase("fieldkitdb");

// Keycloak — the identity provider (ADR-0008). FieldKit never stores passwords.
//
// A tenant *is* a realm (realm-per-tenant), so the dev tenant is imported from source rather than
// clicked together by hand: see realms/README.md. Admin credentials are Aspire parameters in
// user-secrets, never in source.
var keycloak = builder.AddKeycloak("keycloak");

if (builder.ExecutionContext.IsRunMode)
{
    // Development: realms by bind mount, and deliberately **no** data volume — Keycloak skips
    // importing a realm that already exists, so persisted state would silently ignore edits to the
    // realm file. Losing the realm on every restart is the feature here.
    keycloak.WithRealmImport("./realms");
}
else
{
    // Publish: the same realms, inside the image.
    //
    // The bind mount above cannot deploy. It published as an absolute path on the machine that ran
    // the publisher — `D:/…/FieldKit.AppHost/realms` — named as the source of the identity
    // provider's entire configuration, in a manifest destined for a container app with no such
    // filesystem. See keycloak/Dockerfile.
    keycloak.WithDockerfile(contextPath: ".", dockerfilePath: "keycloak/Dockerfile");

    // A database, because `start` with none falls back to Keycloak's H2 dev-file store — and with
    // no volume behind it, every restart or redeploy of the identity provider forgets everything
    // that is not in the realm import. It shares the managed Postgres ADR-0011 already pays for:
    // free at the margin, and covered by the point-in-time restore that ADR's RPO ≤ 5 min claim
    // rests on.
    //
    // Not applied in development on purpose. Persisting Keycloak there would defeat the realm
    // re-import above, which is how a change to a realm file is seen at all.
    var keycloakDb = postgres.AddDatabase("keycloakdb");

    // `WaitFor` without `WithReference`. The reference would inject seven `KEYCLOAKDB_*` variables
    // Keycloak does not read, one of them a `postgresql://postgres:<password>@…` URI — a second
    // copy of the database password in the identity provider's environment, to be read by nothing.
    // Keycloak takes its database configuration through `KC_DB_*`, which is set explicitly below.
    keycloak
        .WaitFor(keycloakDb)
        .WithEnvironment("KC_DB", "postgres")
        .WithEnvironment("KC_DB_URL", ReferenceExpression.Create(
            $"jdbc:postgresql://{postgres.Resource.PrimaryEndpoint.Property(EndpointProperty.Host)}:{postgres.Resource.PrimaryEndpoint.Property(EndpointProperty.Port)}/keycloakdb"))
        // A parameter only when one was supplied. Aspire's default is the literal `postgres`, which
        // is what the API's own connection string in this manifest already carries.
        .WithEnvironment("KC_DB_USERNAME", postgres.Resource.UserNameParameter is { } username
            ? ReferenceExpression.Create($"{username}")
            : ReferenceExpression.Create($"postgres"))
        .WithEnvironment("KC_DB_PASSWORD", postgres.Resource.PasswordParameter);

    // Behind Azure Container Apps' ingress, which terminates TLS and forwards over plain HTTP.
    //
    // Without these, Keycloak builds its issuer and its redirect URLs from what it believes its own
    // address to be — the container's internal one. The browser is then sent to a host it cannot
    // reach, and any token that *is* minted carries an issuer the API has never heard of. That is
    // the failure the note on the front end's Keycloak reference already warns about, arriving by a
    // different route: a 401 that looks nothing like a configuration mistake.
    keycloak
        .WithEnvironment("KC_PROXY_HEADERS", "xforwarded")
        .WithEnvironment("KC_HTTP_ENABLED", "true")
        .WithEnvironment("KC_HOSTNAME", keycloak.GetEndpoint("http"));
}

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

// The address Keycloak will send the browser back to after a login.
//
// The realm files carry `${FIELDKIT_WEB_ORIGIN:http://localhost:3000}` wherever an origin appears —
// redirect URIs, web origins, post-logout — so development needs nothing set and the deploy needs
// one variable. Before D4 they carried the literal `http://localhost:3000`, which would have sent
// every visitor to the deployed demo back to their own machine at the end of sign-in.
//
// Set here rather than beside the other Keycloak settings because it names the *front end*, which
// is declared below it. Publish only: in development the default in the file is already right, and
// asking Keycloak for the front end's address while the front end waits for Keycloak is a cycle.
if (builder.ExecutionContext.IsPublishMode)
    keycloak.WithEnvironment("FIELDKIT_WEB_ORIGIN", frontend.GetEndpoint("http"));

/*
 * The scale rules the costing is made of (ADR-0011).
 *
 * ADR-0011 priced this deployment at ≈ $11–16/month on the strength of three numbers: Keycloak
 * pinned to one replica, the API and the front end at zero. **None of them existed anywhere but in
 * that document.** The bill is not decided by the ADR; it is decided by whatever `minReplicas` the
 * generated infrastructure happens to carry, and nothing here was setting it.
 *
 * That is the same shape of gap as the three the earlier deploy slices found — a decision recorded
 * in prose, with no artifact expressing it — and it is the one that costs money rather than
 * failing loudly.
 */
if (builder.ExecutionContext.IsPublishMode)
{
    // Zero replicas when nobody is looking. Both cold-start in seconds, and a portfolio demo is
    // idle almost always — this is the line that makes "cheap enough to leave running" true.
    server.PublishAsAzureContainerApp((_, app) =>
    {
        app.Template.Scale.MinReplicas = 0;
        app.Template.Scale.MaxReplicas = 1;
    });

    frontend.PublishAsAzureContainerApp((_, app) =>
    {
        app.Template.Scale.MinReplicas = 0;
        app.Template.Scale.MaxReplicas = 1;
    });

    // Keycloak stays warm, and is the only thing here that costs anything.
    //
    // It cannot usefully scale to zero: it is on the login path, so the first visitor would pay a
    // 30–60 s JVM cold start at the exact moment a reader forms an opinion of the project. One
    // replica rather than more because sessions live in Infinispan — a second replica needs
    // clustering configured, which is a decision this demo has not made and does not need.
    keycloak.PublishAsAzureContainerApp((_, app) =>
    {
        app.Template.Scale.MinReplicas = 1;
        app.Template.Scale.MaxReplicas = 1;
    });
}

#pragma warning restore ASPIREJAVASCRIPT001

builder.Build().Run();
