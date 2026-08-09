# ADR-0011: Deployment target — Azure Container Apps

- **Status:** Accepted
- **Date:** 2026-08
- **Deciders:** Tiberiu Socea
- **Related:** [ADR-0003](0003-adopt-dotnet-aspire.md),
  decision [A6](../../product/decisions-and-assumptions.md#a6--hosting--live-demo-azure-container-apps-via-aspire-deploy)

## Context

The portfolio needs a **clickable live demo**, and the build already uses Aspire
([ADR-0003](0003-adopt-dotnet-aspire.md)). The target should show an end-to-end cloud-native
deploy story, align with the Azure/AKS background on the CV, and stay cheap enough to leave
running.

## Decision

Deploy to **Azure Container Apps (ACA)**, published from Aspire (`aspire deploy` / `azd`).

- `FieldKit.Server` and the Next.js app run as **container apps**; Aspire generates the manifest
  from the AppHost model.
- Backing services (📝 assumptions, [A6](../../product/decisions-and-assumptions.md#a6--hosting--live-demo-azure-container-apps-via-aspire-deploy)):
  **Azure Database for PostgreSQL** (flexible server), **Azure Cache for Redis** (or a Redis
  container app to cut cost), **Azure Blob Storage** for photos, **Keycloak** as a container app.
- **Scale-to-zero** on the app containers when idle to keep the demo near-free.
- Telemetry ships via **OTLP** to a backend (Azure Monitor or a self-hosted OTel collector) —
  [observability](../15-observability.md).
- **Backup / DR:** managed Postgres **point-in-time restore** (target **RPO ≤ 5 min, RTO ≤ 1 h**);
  object storage (photos) is geo-redundant; the outbox + idempotency design means a restore that
  loses the last few minutes is re-driven safely by device re-push. Config/infra is reproducible
  from the Aspire manifest (infra-as-code), so the stateless tier is rebuilt, not restored.

## Options considered

| Option | Verdict | Why |
|---|---|---|
| **Azure Container Apps** | **Chosen** | First-class Aspire publishing; scale-to-zero; managed backing services; matches CV's Azure story. |
| Azure Kubernetes Service | Rejected | More control than a solo demo needs; higher baseline cost & ops. |
| Fly.io / Render | Rejected (for demo) | Cheap and simple, but weaker Aspire integration and off-narrative for the Azure showcase. |
| App Service | Rejected | Less natural fit for the multi-container Aspire model. |

## Consequences

**Positive**
- One model dev→prod ([ADR-0003](0003-adopt-dotnet-aspire.md)); deployment is `aspire deploy`.
- Scale-to-zero keeps a persistent demo affordable.
- Managed Postgres/Redis/Blob remove operational burden.

**Negative / costs**
- A running demo incurs **some Azure cost** (mitigated by scale-to-zero and a Redis container).
- Cold-start latency on scale-from-zero — acceptable for a portfolio demo.
- Ties the deploy path to Azure specifics (revisitable; the container images are portable).

**Follow-up:** ~~finalize the exact managed-vs-container split for backing services at deploy time~~
— done, below. Still open: document the CI/CD pipeline (GitHub Actions → build/test/arch-test →
image → `aspire deploy`).

## Costing and the backing-service split (2026-08)

The decision above says "stay cheap enough to leave running" without saying what that costs. Priced
before the first deploy, because a number nobody checked is how a demo quietly becomes £40/month.

**Rates used** (East US, 730 h/month, [ACA pricing](https://azure.microsoft.com/en-us/pricing/details/container-apps/)):
active `$0.000024`/vCPU-s and `$0.000003`/GiB-s; **idle** `$0.000008` and `$0.000001`; free grant
180,000 vCPU-s + 360,000 GiB-s + 2M requests per subscription per month. Rates move — treat the
totals as the shape of the bill rather than the bill.

| Line | Sizing | $/month |
|---|---|---|
| Keycloak, `minReplicas: 1` | 0.5 vCPU / 1 GiB | **11.34 idle · 34.02 active** |
| `server` + `webfrontend`, `minReplicas: 0` | scale-to-zero | ~0 |
| PostgreSQL flexible server | B1ms, free-account offer | **0 for 12 months**, then ~12–15 + storage |
| Container images | GHCR (public) | **0** — ACR Basic would be 5.08 |
| Log Analytics | first 5 GB/month free | ~0 while logging stays quiet |
| Redis | **dropped for the demo** | 0 — Azure Cache C0 would be ~16 |

**≈ $11–16/month in year one; ≈ $26–31 after the free Postgres period.**

### The split, decided

- **PostgreSQL: managed.** The [12-month free-account offer](https://learn.microsoft.com/en-us/azure/postgresql/flexible-server/how-to-deploy-on-azure-free-account)
  (750 h B1ms + 32 GB) makes it free for a year, and it is what this ADR's own RPO ≤ 5 min claim
  rests on — point-in-time restore is the reason to pay for a database rather than run one.
- **Keycloak: container app, `minReplicas: 1`.** It cannot usefully scale to zero: it is on the
  login path, so the first visitor would pay a 30–60 s cold start at the exact moment a reader
  forms an opinion of the project.
- **`server` and `webfrontend`: `minReplicas: 0`.** Both cold-start in seconds, and a demo is idle
  almost always.
- **Redis: not deployed — and now not built either.** It backed output caching only, and the first
  deploy slice found that *nothing had ever opted in*: no endpoint called `.CacheOutput()`. The
  cache, the client registration and the AppHost resource are removed rather than made optional, so
  the deployed shape and the dev shape agree instead of differing by a container. Redis returns in
  W8 with the sync idempotency ledger, which is a consumer rather than a placeholder — and that
  ledger gets its own costing then (a Redis container app ≈ $11/month against a Postgres-backed
  ledger at no extra cost, on a database that is already there).
- **Images: GHCR, not ACR.** The images are public anyway; ACR Basic is $5/month for a private
  registry nothing here needs.

### The number that is not settled

**Whether an idle Keycloak qualifies for idle billing**, which is a 3× swing on the only line that
costs anything. [The billing rules](https://learn.microsoft.com/en-us/azure/container-apps/billing)
require a replica to use **< 0.01 vCPU** and receive **< 1,000 bytes/second**; a resting Keycloak
JVM — Infinispan heartbeats, session-expiry sweeps — may sit either side of that. No document
settles it. **Verify against a week of real billing after the first deploy**, and revisit this
section with the answer.

Dropping Keycloak to 0.25 vCPU / 0.5 GiB would roughly halve it, and is deliberately **not** taken:
512 MiB is below what Keycloak wants in production mode, and a demo that OOMs to save $6 is a worse
outcome than the $6.

### The option this re-examined

A single **Hetzner CX22** (2 vCPU / 4 GB, ~€4.35/month including IPv4 and 20 TB traffic) running all
five containers under Compose costs **roughly a quarter** of the ACA shape, and about a fifth once
the Postgres free year ends — ~$130 against ~$560 over 24 months.

It stays rejected, and the reasoning is worth recording because the ratio is genuinely unflattering:
ACA is bought for `aspire deploy` from the existing AppHost model, managed Postgres with real
point-in-time restore, and the Azure story that is half of what this project is *for*. The VPS also
moves patching, TLS renewal and backup verification onto the author — unpaid time that never appears
in the €4. At $16/month the absolute cost is small enough that the multiple is the wrong thing to
optimise. **If the Keycloak idle question resolves badly (~$39/month), this is worth revisiting.**
