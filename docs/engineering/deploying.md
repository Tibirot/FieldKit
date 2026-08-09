# Deploying FieldKit

The live demo runs on **Azure Container Apps**, published from the Aspire AppHost
([ADR-0011](../architecture/adr/0011-deployment-azure-container-apps.md)). This page is the runbook:
what has to be true first, what the command does, and what to check afterwards.

> **Nothing here has been deployed yet.** Every step below was prepared and verified locally —
> generated infrastructure read, images built and run, the app exercised — but no `aspire deploy`
> has been executed against a real subscription. Treat the first run as the test of this page.

## Prerequisites

| | Needed | How to check |
|---|---|---|
| Azure subscription | any, with permission to create a resource group | `az account show` |
| Azure CLI | signed in | `az login` |
| Docker | running — images are built locally and pushed | `docker info` |
| **Aspire CLI** | **13.4.x**, matching `Aspire.AppHost.Sdk` | `aspire --version` |

**The Aspire CLI version is a real trap, not a formality.** The AppHost pins
`Aspire.AppHost.Sdk/13.4.6`, and the machine this was prepared on had CLI **9.5.2** — four majors
behind. The csproj already carries a comment about the matching failure mode in the other direction
(an older SDK under a newer CLI fails at launch, *after* a clean build). Update before starting:

```bash
dotnet tool update -g aspire.cli
```

## What gets created

Publishing generates real infrastructure-as-code — inspect it without deploying anything:

```bash
dotnet run --project FieldKit.AppHost -- --publisher manifest --output-path ./artifacts/manifest.json
```

That writes `manifest.json`, a `webfrontend.Dockerfile`, and one `*.module.bicep` per resource. Read
them. The bicep is what actually runs, and every deploy surprise found so far has been visible there
first.

| Resource | Shape | Notes |
|---|---|---|
| `fieldkit-env` | Container app environment | Log Analytics workspace attached |
| `fieldkit-env-acr` | **ACR Basic** | ~$5.08/month; not optional — see [ADR-0011](../architecture/adr/0011-deployment-azure-container-apps.md#the-split-decided) |
| `server` | Container app, `minReplicas: 0` | scale-to-zero |
| `webfrontend` | Container app, `minReplicas: 0` | built from `frontend/Dockerfile` (generated) |
| `keycloak` | Container app, `minReplicas: 1` | warm: it is on the login path |
| `postgres` | **Azure Database for PostgreSQL flexible server** | B1ms / Burstable, 32 GB, v16, 7-day backup retention |
| `postgres-kv` | Key Vault | holds the connection secret; a managed identity per consumer reads it |

`scripts/check-deploy-manifest.mjs` asserts this shape on every CI run, including the replica counts
— those are the entire costing, and a wrong one deploys perfectly and shows up as a larger invoice
six weeks later.

## Secrets

Two, both already `secret: true` parameters in the manifest, both generated if not supplied:

| Parameter | What it is |
|---|---|
| `postgres-password` | the database administrator password |
| `keycloak-password` | Keycloak's **bootstrap admin** password — used once, on an empty database |

The database administrator *login* is not a secret and not generated: it is the literal `fieldkit`
(`postgres-username`). Azure documents the flexible server's admin login as unable to be a "system
reserved name" without listing them, and `postgres` — a database the service creates for itself —
is exactly the ambiguous case, so it is avoided.

> **In development this changed the Postgres superuser**, and a data volume is initialised once with
> whatever superuser existed then. The AppHost therefore mounts a volume under a new name
> (`fieldkit-postgres-data`); the old one is left in place, unmounted, and can be removed with
> `docker volume rm` whenever its contents stop being interesting. Nothing is lost automatically.

Supply them explicitly rather than letting the deploy generate them, or you will not have the
Keycloak admin password when you need it:

```bash
aspire config set Parameters:keycloak-password "<a password you have stored>"
```

Never commit either. In development they live in user-secrets
([security §6](../architecture/16-security.md#6-application-security-baseline)).

## Deploying

```bash
aspire deploy
```

It will ask for a subscription, a location and a resource group on first run, and cache them. Expect
**15–25 minutes** for the first deploy — most of it provisioning the environment and the registry,
not building images.

## After the first deploy

1. **Sign in.** The whole point. A failure here is almost certainly Keycloak's view of its own
   address — check `KC_HOSTNAME` on the `keycloak` container app resolved to its public FQDN, and
   that the realm's redirect URI is the front end's FQDN and not `localhost:3000`
   ([realms/README.md](../../FieldKit.AppHost/realms/README.md)).
2. **Confirm the `X-Forwarded-*` headers arrive.** `KC_PROXY_HEADERS=xforwarded` is set on the
   assumption that ACA's ingress sends them. That assumption is documented and **has not been
   tested against a real ingress** — the local verification in D4 could only prove the setting's
   effect, not the header path.
3. **Watch a week of billing**, then update
   [ADR-0011's open question](../architecture/adr/0011-deployment-azure-container-apps.md#the-number-that-is-not-settled):
   whether an idle Keycloak qualifies for ACA's *idle* rate is a 3× swing on the only line that
   costs anything, and no document settles it. Cost Analysis, filtered to the resource group,
   grouped by resource.
4. **Scale-to-zero is observable**: leave it alone for ten minutes, then load the app and time the
   first response. Seconds is expected. Anything worse is worth recording before it becomes the
   thing a reader notices first.

## Tearing it down

```bash
az group delete --name <resource-group> --yes
```

The whole deployment is in one resource group by design. Nothing here is stateful that is not
reproducible from this repository — realms are imported from source, and the database is created by
migrations on first start.
