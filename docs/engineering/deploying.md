# Deploying FieldKit

The live demo runs on **Azure Container Apps**, published from the Aspire AppHost
([ADR-0011](../architecture/adr/0011-deployment-azure-container-apps.md)). This page is the runbook:
what has to be true first, what the command does, and what to check afterwards.

> **Deployed, 2026-08-11**, at commit **`cbbb890`** (W9 + W10). Previously 2026-08-09 at `fb31f75`.
> Resource group `FieldKit`, **Sweden Central**, running at
> [webfrontend.jollysmoke-c6d79515.swedencentral.azurecontainerapps.io](https://webfrontend.jollysmoke-c6d79515.swedencentral.azurecontainerapps.io).
> Everything below has now been executed rather than reasoned about, and the corrections that took
> are marked where they belong.
>
> **Record the commit on every deploy.** It is the only way to answer "what is actually out there",
> and [Redeploying](#redeploying) needs it to compute what a redeploy will *not* carry.

## Prerequisites

| | Needed | How to check |
|---|---|---|
| Azure subscription | any, with permission to create a resource group | `az account show` |
| Azure CLI | signed in | `az login` |
| Docker | running — images are built locally and pushed | `docker info` |
| **Aspire CLI** | **13.4.x**, matching `Aspire.AppHost.Sdk` | `aspire --version` |

**The Aspire CLI version is a real trap, not a formality.** The AppHost pins
`Aspire.AppHost.Sdk/13.4.6`, and this machine reported CLI **9.5.2** during D5 prep — four majors
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
Keycloak admin password when you need it. **They live in the AppHost's user-secrets** — the same
place development reads them from, and the same place `aspire deploy` resolves `Parameters:<name>`
from on the machine that runs it:

```bash
dotnet user-secrets set "Parameters:keycloak-password" "<a password you have stored>" --project FieldKit.AppHost
```

> **Not `aspire config set`.** This page said that until the first person tried it: `aspire config`
> manages **CLI settings and feature flags**, and has nothing to do with AppHost parameters. Writing
> a password there would leave it out of the deployment entirely — and the deploy would silently
> generate one instead, which is the exact outcome this section exists to prevent.

**`keycloak-password` is a *bootstrap* password**, and this is why it has to be set before the first
deploy rather than after. `KC_BOOTSTRAP_ADMIN_PASSWORD` is only read against an **empty** Keycloak
database; once the admin account exists, the parameter is inert and a change has to be made in the
Keycloak admin console. Development is the exception and only by accident — Keycloak keeps no state
there, so it re-bootstraps on every start.

Never commit either. In development they live in user-secrets
([security §6](../architecture/16-security.md#6-application-security-baseline)).

## Deploying

```bash
aspire deploy
```

It will ask for a subscription, a location and a resource group on first run, and cache them in
`~/.aspire/deployments/<hash>/production.json` — **including the parameter values in cleartext**,
which is worth knowing before that machine is shared or backed up.

**Timings, measured:** the full first provisioning ran about **8 minutes**; a redeploy that only
rebuilds images and updates the container apps takes **1–2 minutes** (`37/37 steps · 1m 14s`, twice —
2026-08-09 and again on 2026-08-11, the second carrying 17 migrations and a new schema).

**Non-interactive** is worth knowing if you are scripting or running this through a tool: once the
first run has cached a subscription, location and resource group, `aspire deploy --non-interactive
--nologo` needs no input at all.

### Choosing a location

**Not West Europe.** It refused the first attempt outright:

```
RequestDisallowedByAzure: The selected region is currently not accepting new customers
```

That is capacity, not your account — and West and North Europe are the two most commonly gated for
new subscriptions. **Sweden Central** took it. If you need to change region after the first run,
edit `Azure:Location` in the deployment-state file above; `aspire deploy --clear-cache` re-prompts
for everything instead.

## After the first deploy

0. **Clear the service worker before you believe anything.** This app is a PWA, and its worker
   **deliberately does not skip waiting**: activating early would delete the running build's chunks
   out from under a page mid-visit, which for a rep with an unsynced outbox is the worst possible
   moment (`sw/index.js`). The swap happens when the page asks for it, and the prompt that asks
   arrives with the field shell — so until then, a browser that has visited before keeps running
   **the previous build** after a deploy, no matter how many times you reload.

   This cost real time during the first deploy: a fix was verified as live by `curl`, and the same
   browser kept reproducing the bug from precache. Verify in a fresh private window, or:

   ```js
   (async () => {
     for (const r of await navigator.serviceWorker.getRegistrations()) await r.unregister();
     for (const n of await caches.keys()) await caches.delete(n);
   })()
   ```

   The quickest independent check that a new build is live is the CSP header, which is rendered per
   request and never cached: `curl -sD - <frontend>/en/login | grep -i content-security-policy`.

1. **Sign in.** The whole point. A failure here is almost certainly Keycloak's view of its own
   address — check `KC_HOSTNAME` on the `keycloak` container app resolved to its public FQDN, and
   that the realm's redirect URI is the front end's FQDN and not `localhost:3000`
   ([realms/README.md](../../FieldKit.AppHost/realms/README.md)).

   **Then leave it for six minutes.** Signing in exercises a navigation; staying signed in exercises
   a background fetch to Keycloak's discovery document, and only the second one catches a
   browser-facing address that is wrong. That distinction cost two deploys — the app signed in
   perfectly and then reported "Your session has expired" at the first token renewal.
2. ~~**Confirm the `X-Forwarded-*` headers arrive.**~~ **Confirmed, 2026-08-09.** The realm's
   discovery document reports
   `issuer: https://keycloak.<env>.azurecontainerapps.io/realms/fieldkit-dev` — the public FQDN, not
   the container's internal one. Keycloak can only build that from forwarded headers, so
   `KC_PROXY_HEADERS=xforwarded` and `KC_HOSTNAME` are both doing their job. Re-check it here if the
   ingress is ever reconfigured; it is a one-line `curl` of `.well-known/openid-configuration`.
3. **Watch a week of billing**, then update
   [ADR-0011's open question](../architecture/adr/0011-deployment-azure-container-apps.md#the-number-that-is-not-settled):
   whether an idle Keycloak qualifies for ACA's *idle* rate is a 3× swing on the only line that
   costs anything, and no document settles it. Cost Analysis, filtered to the resource group,
   grouped by resource.
4. **Scale-to-zero is observable**: leave it alone for ten minutes, then load the app and time the
   first response. Seconds is expected. Anything worse is worth recording before it becomes the
   thing a reader notices first.

## Redeploying

`aspire deploy` again. It rebuilds the images and updates the container apps — 1–2 minutes — and
carries **two** kinds of change automatically:

- **Code**, in the images.
- **Database schema.** Each module applies its own EF migrations on startup
  (`ModuleMigrator<TContext>`, ADR-0005), so a redeploy migrates the live database as a side effect
  of the server booting. Read the new migrations before you deploy them.

  **A migration that throws takes the whole server down**, and that is the good news: `ModuleMigrator`
  is an `IHostedService`, an exception in `StartAsync` propagates out of host startup, and the
  container app crash-loops with an unhealthy revision. There is no partial state where the app
  serves and one module quietly 500s. So **"the server is healthy" is itself the migration check** —
  `az containerapp revision list --name server -g <rg>` answers it in one line, and does not depend
  on catching the startup log before it rolls.

  *(Recorded 2026-08-11: this bullet previously claimed the opposite — that a failed migration left
  the app up and one module unusable. It does not, and the true behaviour is easier to act on.)*

### A realm change is not deployed by deploying

**This is the one that does not announce itself.** Editing `FieldKit.AppHost/realms/*.json`, building
the image and running `aspire deploy` puts the new file inside the running container **and changes
nothing**. Keycloak imports a realm with `IGNORE_EXISTING`: if the realm is already in its database,
the file is skipped. The deployed Keycloak keeps a Postgres database, so it always is.

Development is the opposite by deliberate design — the Keycloak resource has **no data volume**
([realms/README.md](../../FieldKit.AppHost/realms/README.md#no-data-volume-by-design)), so every
start re-imports and every edit takes immediately. That is exactly why this is invisible until it is
deployed: the workflow that proves the change locally is the one that cannot reproduce the problem.

**Symptom:** a permission-gated endpoint answers **403** in the deployed demo for a user whose realm
file grants the role. Nothing else is wrong; the token simply does not carry a role Keycloak never
heard of.

> **Outstanding as of W11 slice 4a: `order:reject`.** The realm files grant it to `admin`, and the
> deployed realm has never heard of it — so `POST /api/orders/{id}/rejection` answers `403` there
> until the role is added by hand. It is the first role minted since the deploy on 2026-08-11, and
> it is named here rather than left for whoever meets the 403 to work out.

**Check before deploying** — realm commits since what is live:

```bash
git log <deployed-commit>..HEAD --oneline -- FieldKit.AppHost/realms/
```

**Or ask the deployed realm directly**, which is the answer that cannot be stale. With an admin token
from the master realm:

```bash
curl -s -H "Authorization: Bearer $TOKEN" https://<keycloak-fqdn>/admin/realms/fieldkit-dev/roles | grep -o '"name":"[^"]*"'
```

**The fix is not another deploy.** Pick one:

| | When | How |
|---|---|---|
| Add by hand | a role or two — the usual case | Keycloak admin console → *Realm roles* → create, then *Users* → assign. Minutes, and no downtime. |
| Re-import | the realm has drifted badly, or a client's redirect URIs changed | Delete the realm in the console, restart the `keycloak` container app so `--import-realm` sees it missing. **Destroys anything created in the console**, including users added by hand. |

Re-import is the honest reset and it is rarely what you want mid-demo: the realm file is the source of
truth, but the deployed realm is where a hand-added account lives, and nothing reconciles the two.
Realm **provisioning** through the admin API (`IAM-10`, Phase 2) is what eventually removes this
whole section.

> **Known drift, as of 2026-08-11.** `device:read` and `device:revoke` were added to the realm in W8
> slice 2 (#169), after the `fb31f75` deploy, and the live Keycloak does not have them. Impact is
> confined to an administrator revoking a device over the API: the field app binds with
> `POST /api/sync/devices`, which deliberately requires a token and **no** permission, and no screen
> calls the other two.

### The service worker will serve the old build

Covered under [After the first deploy](#after-the-first-deploy), and it matters more on a redeploy
than on a first one — a browser that has visited before has a precache. Verify in a fresh private
window, and remember `curl` of the CSP header is the check that cannot be cached.

## Tearing it down

```bash
az group delete --name <resource-group> --yes
```

The whole deployment is in one resource group by design. Nothing here is stateful that is not
reproducible from this repository — realms are imported from source, and the database is created by
migrations on first start.
