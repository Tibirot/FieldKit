# Observability

> **Status:** ✅ Baseline · **Last updated:** 2026-08
> **Decision:** [ADR-0003](adr/0003-adopt-dotnet-aspire.md) · **Foundation:** `ServiceDefaults` (scaffolded)

FieldKit treats observability as a **default, not a retrofit** — the CV highlights performance
work, and this is where that shows. The `ServiceDefaults` project already wires OpenTelemetry,
health checks, and resilient HTTP into every service ([Extensions.cs](../../FieldKit.Server/Extensions.cs));
this doc covers what we get for free and the **domain-specific** signals we add.

## 1. The three signals (OpenTelemetry)

| Signal | Out of the box (service defaults) | FieldKit adds |
|---|---|---|
| **Traces** | ASP.NET Core + HttpClient spans; health checks filtered out | Spans for **sync pull/push**, **outbox dispatch**, and **pricing resolution**; `mutationId`/`tenantId` as span attributes |
| **Metrics** | ASP.NET Core, HttpClient, runtime instrumentation | Domain metrics (below) |
| **Logs** | Structured, formatted, scoped | Correlated to traces via `traceId`; tenant + user scope |

Everything exports via **OTLP**; in dev it surfaces in the **Aspire dashboard**, in prod it ships
to an OTLP backend ([ADR-0011](adr/0011-deployment-azure-container-apps.md)).

## 2. Domain metrics (custom)

The signals that tell you FieldKit is *working*, not just *up*:

| Metric | Type | Why it matters |
|---|---|---|
| `fieldkit.sync.push.batch_size` | histogram | Field workload per reconnect |
| `fieldkit.sync.push.latency` | histogram | How fast a day's work reconciles |
| `fieldkit.sync.mutations.rejected` | counter (by reason) | Data-quality / rule-rejection signal |
| `fieldkit.outbox.backlog` | gauge | Cross-module event health — **alertable** ([ADR-0006](adr/0006-in-process-messaging-and-outbox.md)) |
| `fieldkit.outbox.dispatch.latency` | histogram | Eventual-consistency lag |
| `fieldkit.visits.completed` | counter (by tenant) | Business throughput |
| `fieldkit.orders.submitted.value` | histogram | Commercial signal |
| `fieldkit.pricing.resolve.duration` | histogram | Perf of the hot pricing path |
| `fieldkit.photos.upload.pending` | gauge | Binary-sync backlog |

These double as the **operational KPIs** behind the supervisor dashboards
([reporting](../product/00-product-overview.md#reporting--kpis-cross-cutting-read-side)).

## 3. Health checks

Per `ServiceDefaults`: `/health` (all checks — readiness) and `/alive` (liveness only). Extended
with dependency checks: **PostgreSQL**, **Keycloak reachability**, and **outbox
liveness** (dispatcher heartbeat). Redis joins them in W8, when the sync idempotency ledger gives
it a consumer — a health check on a dependency nothing uses reports on nothing. Health endpoints are dev-open and locked down in non-dev per
Aspire guidance.

## 4. Correlation

- One **`traceId`** flows request → module handler → DB span → outbox, and is returned in every
  `ProblemDetails` ([API contracts](13-api-contracts.md)) — so a user-reported error links straight
  to its trace.
- `tenantId` and (where relevant) `deviceId`/`mutationId` are span/log attributes, making it
  possible to trace **one rep's sync** end to end.

## 5. Client-side (field app)

- A lightweight client log of sync runs (counts, durations, failures) is viewable in-app
  (support) and, when online, can post anonymized sync telemetry to correlate device-side and
  server-side views of a sync.
- **Client crash/error telemetry** — because you can't SSH into a field fleet, the PWA captures
  unhandled errors, service-worker failures, storage-eviction/quota events, and failed-sync reasons
  and ships them (batched, on reconnect) with `deviceId` so a device that's silently failing is
  visible server-side.
- No continuous location tracking is logged — only check-in points ([security/GDPR](16-security.md)).

## 6. Performance targets (not just assumptions)

Backing the scale *assumptions* ([B6](../product/decisions-and-assumptions.md#b6--scale-assumptions-representative-not-limits))
with *targets* worth measuring against:

| Target | Budget |
|---|---|
| Territory pull payload (delta, steady state) | ≤ ~1–2 MB |
| Full territory snapshot (bind) | ≤ ~a few MB (fits IndexedDB comfortably) |
| Sync **push** p95 latency (normal day's outbox) | seconds after a good reconnect |
| **Reconnect burst** (200 reps at shift start) | absorbed via batch-size caps + warm-up, not scale-from-zero ([arch §7](00-architecture-overview.md#7-deployment-topology)) |
| Pricing/score resolve (hot path) | sub-ms on device |

## 7. What "good" looks like

- Any error a user sees is reproducible from its `traceId` within one trace.
- Outbox backlog ≈ 0 in steady state; a rising backlog alerts before it becomes user-visible.
- Sync push p95 latency and rejection rate are dashboarded per tenant; the targets above have alerts.
