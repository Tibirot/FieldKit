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

**Follow-up:** finalize the exact managed-vs-container split for backing services at deploy time;
document the CI/CD pipeline (GitHub Actions → build/test/arch-test → image → `aspire deploy`).
