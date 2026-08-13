var builder = DistributedApplication.CreateBuilder(args);

// No Redis, and it is not coming back. It was here from W1 backing an output cache nothing ever
// used, and it left dev running a container the deploy would not have (ADR-0011 prices it as the
// largest avoidable line).
//
// This comment used to promise it would return in W8 with the sync idempotency ledger. W8 decided
// otherwise: the ledger is a Postgres table, because a dedupe check is one indexed read on a
// database this deployment already runs, and a Redis container app would be ~$11/month against a
// total bill of ~$16–21 — the second-largest line on the invoice, bought for latency nothing here
// can measure. See the ADR-0007 amendment (2026-08).

// The Azure Container Apps environment everything below is published into (ADR-0011).
//
// Declared rather than left to `azd` to invent, because the environment is where the cost decisions
// live — Log Analytics, and the scale rules set per app further down.
//
// **Without the Aspire dashboard**, which this deploys by default as an `AspireDashboard`
// `dotNetComponent` on the environment. It is the only thing that failed on the first deploy that
// got as far as Azure:
//
//     Step 'provision-fieldkit-env' failed.
//     Failed to provision component 'aspire-dashboard'. Error details: Internal Server Error.
//
// Retried once by the pipeline, failed the same way, and took four minutes each time — a 500 from
// the `2025-10-02-preview` API rather than anything this repository controls.
//
// Turned off rather than worked around, because the demo does not want it either way: the dashboard
// is a second public endpoint serving this system's traces and logs, and ADR-0011 already sends
// telemetry out over OTLP. Development is untouched — `dotnet run` still brings up the dashboard,
// which is where it is actually used.
builder.AddAzureContainerAppEnvironment("fieldkit-env")
    .WithDashboard(false);

/*
 * The database credentials, named here rather than left to the integration to invent.
 *
 * `AddAzurePostgresFlexibleServer` would create both itself, and then nothing in this file could
 * refer to them — which matters because Keycloak needs the username and password *by value*, as
 * `KC_DB_USERNAME` and `KC_DB_PASSWORD`, and has no other way to reach a database.
 *
 * Both generate a value on first use and persist it to user-secrets, which is what `AddPostgres`
 * did for the password before this. A fresh clone therefore still needs no configuration: the first
 * `dotnet run` writes a password, and every later one reads the same one back — the data volume
 * outlives the process, so a password regenerated per run would lock the developer out of their own
 * database on the second start.
 */
// A fixed name, not a generated one, and not `postgres`.
//
// **Not generated**, though the password is: a random admin username buys nothing once the password
// is a secret, and it costs a name that appears in pgweb, in `psql` invocations, in every log line
// about a failed connection, and in the runbook. `Role "yFBEpmbuSdfh" does not exist` is a real
// error this produced, and it is worse in every way than the same error naming `fieldkit`.
//
// **Not `postgres`**, which would have kept existing dev volumes working, because Azure documents
// the flexible server's admin login as unable to be "system reserved names" without saying which —
// and `postgres`, a database the service creates for itself, is exactly the ambiguous case. A name
// that might be refused at provisioning is not worth the convenience.
var postgresUsername = builder.AddParameter("postgres-username", "fieldkit");

var postgresPassword = builder.AddParameter(
    "postgres-password",
    // 22 characters, matching what `AddPostgres` generated. No specials: this value travels through
    // a JDBC URL to Keycloak, where `;` and `&` change what the string means.
    new GenerateParameterDefault { MinLength = 22, Special = false },
    secret: true,
    persist: true);

// PostgreSQL — the system of record. One database; each module owns a schema (ADR-0005).
//
// **Managed when published, a container in development.** ADR-0011 chose Azure Database for
// PostgreSQL for point-in-time restore, which is what its RPO ≤ 5 min claim rests on, and for the
// 12-month free-tier year the costing assumes. Until this slice it published as an ordinary
// container app with `minReplicas: 1`: no restore, no free year, and an always-on container nobody
// had budgeted. `RunAsContainer` keeps `dotnet run` as it was — data volume, pgweb, no Azure
// account needed to work on the app.
//
// Password authentication rather than the Entra ID default. The API could use a managed identity;
// Keycloak cannot — it takes a JDBC URL with a username and password and has no token path — and
// one database with two authentication modes is a worse answer than one with the mode both its
// consumers share.
var postgres = builder.AddAzurePostgresFlexibleServer("postgres")
    .WithPasswordAuthentication(postgresUsername, postgresPassword)
    .RunAsContainer(container => container
        // **A named volume, and the name is new.** Postgres runs `initdb` only when the data
        // directory is empty, so a volume created under the old `postgres` superuser cannot
        // authenticate the new one — the container starts and then refuses every connection with
        // `Role "fieldkit" does not exist`. Measured, on the volume this repository had.
        //
        // Naming it afresh sidesteps that without deleting anyone's data: the old volume is simply
        // no longer mounted, and can be removed whenever its contents stop being interesting.
        // Development data here is reproducible anyway — realms are imported from source and
        // schemas are created by migrations on first start.
        .WithDataVolume("fieldkit-postgres-data")
        .WithPgWeb());

var database = postgres.AddDatabase("fieldkitdb");

// Keycloak — the identity provider (ADR-0008). FieldKit never stores passwords.
//
// A tenant *is* a realm (realm-per-tenant), so the dev tenant is imported from source rather than
// clicked together by hand: see realms/README.md. Admin credentials are Aspire parameters in
// user-secrets, never in source.
// **Publicly reachable, and it has to be.** Authorization code + PKCE (ADR-0008) redirects the
// *browser* to Keycloak; an identity provider only the other containers can see cannot authenticate
// anybody. The first successful deploy proved this the expensive way — 37/37 steps green, three
// container apps running, and `keycloak: No public endpoints` in the summary. The front end came up,
// rendered, and answered a sign-in attempt with "Couldn't reach the identity provider."
//
// It was visible before that: every published manifest since D4 showed Keycloak's `http` binding
// without `"external": true`, next to a `server` and a `webfrontend` that had it. Nothing read them.
// `WithExternalHttpEndpoints()` is what `server` and `webfrontend` use and it fails here:
// `AddKeycloak` declares two http endpoints, `http` (8080) and `management` (9000), and that helper
// marks **both** external — which a container app refuses outright, "Multiple external endpoints are
// not supported". Naming the one endpoint is therefore not a workaround but the correct thing: 9000
// serves health and metrics and has no business being on the public internet.
var keycloak = builder.AddKeycloak("keycloak")
    .WithEndpoint("http", endpoint => endpoint.IsExternal = true);

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
        // Assembled from the server's host name, and **not** from `keycloakDb.JdbcConnectionString`
        // — which is the obvious choice and the wrong one. That property appends
        // `authenticationPluginClassName=…AzurePostgresqlAuthenticationPlugin`, an Entra ID plugin
        // that (a) contradicts the password authentication configured above and (b) names a class
        // the Keycloak image's driver does not contain, so the container would fail to open a
        // connection at all. Tried it, read the generated manifest, replaced it.
        //
        // `HostName` resolves to the container's host in development and the flexible server's FQDN
        // when published, which is what lets this one expression serve both.
        .WithEnvironment("KC_DB_URL", ReferenceExpression.Create(
            $"jdbc:postgresql://{postgres.Resource.HostName}/keycloakdb?sslmode=require"))
        // The same two parameters the server is provisioned with, by value. Keycloak has no managed
        // identity path; this is the whole reason they are declared at the top of this file rather
        // than left for `AddAzurePostgresFlexibleServer` to generate privately.
        .WithEnvironment("KC_DB_USERNAME", postgresUsername)
        .WithEnvironment("KC_DB_PASSWORD", postgresPassword);

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

/*
 * Object storage for shelf photographs (`OFF-08`, `B5`, W11 slice 12a).
 *
 * <b>Azurite in development, Azure Blob Storage when published</b> — one resource, two runtimes, and
 * the same client code against both. A filesystem stub behind the same interface would have kept the
 * dev graph one container smaller and left the path that actually ships unexercised: presigned URLs
 * are a Blob feature, and a fake that hands back a local path proves nothing about a SAS.
 *
 * <b>Photographs are the one thing here that is not re-fetchable.</b> `ADR-0011` already names object
 * storage as geo-redundant for exactly this reason: the outbox and idempotency design mean a restored
 * database loses no captured work, but a lost blob is a photograph that existed nowhere else once the
 * device has moved on.
 */
var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(emulator => emulator
        // A named volume, so a rep's photographs survive `dotnet run` cycles during development —
        // the same reasoning the Postgres volume above carries, and the same reproducibility caveat.
        .WithDataVolume("fieldkit-azurite-data"));

// One container, not one per tenant: the tenant is the first segment of every object's path, minted
// server-side from the caller's token (see `PhotoEndpoints`). Containers are a coarse unit that would
// have to be created on tenant onboarding and are easy to address across; a path prefix the API
// controls is checked on every request by construction.
var photos = storage.AddBlobs("photos");

var server = builder.AddProject<Projects.FieldKit_Server>("server")
    .WithReference(database)
    .WaitFor(database)
    .WithReference(photos)
    .WaitFor(storage)
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

/*
 * The origin the browser is allowed to `PUT` a photograph to (`OFF-08`, W11 slice 12c).
 *
 * <b>The front end gets a URL, never the connection string.</b> The server needs
 * `ConnectionStrings__photos` because it signs with a credential; the browser needs only an origin
 * for its Content Security Policy, and handing a front end a string containing an account key so it
 * can parse one substring out of it would be putting a secret somewhere it has no business being.
 *
 * <b>Without this the upload does not work at all.</b> `connect-src` names the origins the browser
 * may reach; object storage is not this app's origin, so every `PUT` was refused before a byte left
 * the device — presign succeeded, upload never happened, retry made it look like a bad network. It
 * shipped that way in 12b and a browser check found it, which no test in either suite could: the
 * device tests mock `fetch` and the server tests upload from .NET, where there is no CSP.
 */
frontend.WithEnvironment("PHOTO_STORAGE_URL", storage.GetEndpoint("blob"));

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
 * Keycloak's **browser-facing** address, which is not the one service discovery hands out.
 *
 * `WithReference(keycloak)` injects `services__keycloak__http__0`, and in a container app that is
 * the internal FQDN — `keycloak.internal.<env>.azurecontainerapps.io`. Correct for one container
 * calling another, and useless to a browser, which is who actually talks to an OIDC provider.
 *
 * The deployed app failed on exactly that, in a way that took a console to see:
 *
 *     Access to fetch at 'https://keycloak.internal.…/.well-known/openid-configuration'
 *     from origin 'https://webfrontend.…' has been blocked by CORS policy
 *
 * Signing in still worked — the first hop is a navigation, not a fetch — so the symptom arrived
 * about five minutes later, when the first silent token renewal failed and the app reported "Your
 * session has expired". A login loop with a working login in it.
 *
 * `GetEndpoint("http")` is the same expression `KC_HOSTNAME` uses, and resolves to the public FQDN.
 * Publish only: in development both addresses are the same one.
 */
if (builder.ExecutionContext.IsPublishMode)
    frontend.WithEnvironment("KEYCLOAK_URL", keycloak.GetEndpoint("http"));

/*
 * The tenants whose realms this deployment's Keycloak image carries.
 *
 * A realm is only a trusted issuer if a tenant row claims it — the tenant table *is* the trust list
 * (ADR-0008, realms/README.md). Those rows come from `Iam:SeedTenants` until provisioning lands
 * (`IAM-10`), and that section lives in **appsettings.Development.json**. A container runs as
 * Production, loads none of it, and `TenantSeeder` returns early on an empty list.
 *
 * The result is an API that trusts no realm, and the symptom looks nothing like the cause: sign-in
 * succeeds, Keycloak mints a perfectly good token, every API call is refused with 401, and the front
 * end — correctly — reads 401 as an expired session. What the first working deploy actually did was
 * sign in and then loop on "Your session has expired", with no error anywhere in it.
 *
 * The 688 integration tests all pass because `ServerFixture` boots the host with
 * `UseEnvironment(Development)`. Everything about this path is covered except the environment it
 * runs in.
 *
 * **Set here rather than in an appsettings.Production.json**, because these ids are the other half
 * of the `tenant` claim hardcoded in `realms/*.json` — the directory this same file bakes into the
 * Keycloak image. Two halves of one fact, kept in one place. The values are duplicated from the
 * development config and that is deliberate: the tests boot the server without this AppHost, so
 * that copy cannot be deleted.
 */
if (builder.ExecutionContext.IsPublishMode)
{
    (string Id, string Name, string Realm)[] seedTenants =
    [
        ("00000000-0000-0000-0000-000000000001", "Veridian Beverages (dev)", "fieldkit-dev"),
        ("00000000-0000-0000-0000-000000000002", "Second Tenant (dev)", "fieldkit-dev-b"),
    ];

    for (var index = 0; index < seedTenants.Length; index++)
    {
        var (id, name, realm) = seedTenants[index];

        server
            .WithEnvironment($"Iam__SeedTenants__{index}__Id", id)
            .WithEnvironment($"Iam__SeedTenants__{index}__Name", name)
            .WithEnvironment($"Iam__SeedTenants__{index}__Realm", realm);
    }
}

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
