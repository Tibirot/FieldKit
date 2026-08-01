# ADR-0003: Adopt .NET Aspire for orchestration

- **Status:** Accepted
- **Date:** 2026-08
- **Deciders:** Tiberiu Socea
- **Related:** [ADR-0011](0011-deployment-azure-container-apps.md), [observability](../15-observability.md)

## Context

FieldKit is a monolith *service* but a multi-*process* system in practice: the API, the Next.js
app, PostgreSQL, Redis, and Keycloak all have to come up together, discover each other, and be
observable. Historically that means a hand-maintained `docker-compose.yml`, ad-hoc connection-
string wiring, and bolting on OpenTelemetry. This project also explicitly wants to **showcase
cloud-native .NET** and fill an Aspire gap on the CV.

## Decision

Use **.NET Aspire** as the composition and orchestration layer (`FieldKit.AppHost`).

- **AppHost as composition root:** declaratively provisions PostgreSQL, Redis, and Keycloak
  containers, runs `FieldKit.Server` and the Next.js app, and injects connection strings +
  service-discovery config — no hand-wired endpoints.
- **Service defaults:** the shared `ServiceDefaults` project (already scaffolded) gives every
  service OpenTelemetry (traces/metrics/logs), health checks, and resilient HTTP by default.
- **Aspire dashboard:** live traces, structured logs, and metrics across all resources in dev,
  for free ([observability](../15-observability.md)).
- **Deploy manifest:** the same AppHost model publishes to a real target
  ([ADR-0011: Azure Container Apps](0011-deployment-azure-container-apps.md)) — dev and prod
  described by one model.

## Options considered

| Option | Verdict | Why |
|---|---|---|
| docker-compose + manual wiring | Rejected | Works, but no built-in telemetry/health/discovery; dev and deploy models diverge; nothing to showcase. |
| Raw Kubernetes/Helm locally | Rejected | Heavy dev-loop friction for a solo project; over-kill at this scale. |
| **.NET Aspire** | **Chosen** | First-class composition, service discovery, and OTel out of the box; one model dev→prod; aligns with the cloud-native showcase goal. |

## Consequences

**Positive**
- One command (`dotnet run --project FieldKit.AppHost`) boots the whole system incl. dependencies.
- Observability is a **default**, not a retrofit ([observability](../15-observability.md)).
- Dev composition and deployment come from the same model ([ADR-0011](0011-deployment-azure-container-apps.md)).

**Negative / costs**
- Aspire is young and moves fast — some churn across versions; pinned to a known-good version
  (13.2.x, .NET 10).
- A learning-curve/deliberate-bet dependency — accepted, since demonstrating it is a project goal.

**Neutral**
- Aspire orchestrates dev and *describes* deploy; it is **not** a runtime in production — the
  containers run on the target platform ([ADR-0011](0011-deployment-azure-container-apps.md)).
