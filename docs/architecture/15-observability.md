# Observability

> **Status:** ✅ Baseline · **Last updated:** 2026-08
> **Decision:** [ADR-0003](adr/0003-adopt-dotnet-aspire.md) · **Foundation:** `ServiceDefaults` (scaffolded)

> **What "baseline" means here, read against the code (W13 slice 0).** Every "FieldKit adds" cell in
> §1 and every row of §2 was **specified and unbuilt** — `FieldKit.Server/Extensions.cs` was the
> Aspire template unedited, and the solution declared no `Meter` and no `ActivitySource` of its own.
> Three claims below were stated in the present tense and were not yet true, each owned by a
> [W13 slice](../delivery-plan.md#week-13--observability--security-hardening):
>
> - the **outbox dispatcher heartbeat** in §3 has no dispatcher to hear from — **outstanding**
>   (slice 3);
> - the **`traceId` in every `ProblemDetails`** in §4 appeared nowhere in this repository —
>   **done in slice 2**, and the claim itself was wrong as well as unbuilt: this API does not answer
>   with `ProblemDetails`, so §4 now names the envelope that carries it;
> - the health endpoints in §3 are mapped **only in development** — **outstanding** (slice 5).

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

> **The first three are built (W13 slice 1), and the rules they set stand for the rest.**
>
> **One meter, named `FieldKit`** ([`Telemetry`](../../FieldKit.BuildingBlocks/Telemetry.cs)). A meter
> name is what an exporter subscribes to; one per module would be nine subscriptions to keep in step,
> and the tenth would go missing quietly. Instruments still belong to the area that emits them.
>
> **Tenant is a tag, never part of a name.** "By tenant" above is a dimension of one series —
> `fieldkit.visits.completed.acme` is a new series per customer and cannot be aggregated once.
>
> **Nothing unbounded may be a tag.** Not a mutation id, a device id, a subject, an outlet id, or a
> free-text detail: each is a fresh series per value, which is how the thing meant to warn you is what
> falls over. They belong on a **span**, where one unique value costs one trace. Tenant is the single
> identifier admitted, and only because a tenant is a realm somebody provisions by hand. A refusal
> **code** is admitted on the same test — `ADR-0012` codes are a closed vocabulary declared in source;
> the sentence beside one is not.
>
> **Rejections are counted once per mutation, not once per attempt.** A device that cannot fix a
> mutation retries it until something changes, and counting the replay would turn the rejection rate
> into a measurement of that device's retry policy.
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
liveness** (dispatcher heartbeat). There is no Redis check because there is no Redis: the W8
idempotency ledger it was being held for is a Postgres table ([ADR-0007
amendment](adr/0007-offline-sync-strategy.md#amendment-2026-08-the-ledger-is-postgres-and-there-is-no-redis)),
so the Postgres check already covers it. Health endpoints are dev-open and locked down in non-dev per
Aspire guidance.

## 4. Correlation

- One **`traceId`** flows request → module handler → DB span, and is returned on **every refusal**
  ([API contracts §3](13-api-contracts.md#3-error-model--rfc-7807-problem-details)) — so a
  user-reported error links straight to its trace.
- `tenantId` and (where relevant) `deviceId`/`mutationId` are span/log attributes, making it
  possible to trace **one rep's sync** end to end.

> **Built in W13 slice 2, and the claim above was corrected on the way in.** It used to say the id is
> returned in every `ProblemDetails`; this API does not answer with `ProblemDetails` — every refusal
> it raises uses its own `{ "errors": [...] }` envelope, which had no trace id at all. The wiring now
> exists on both shapes and the sentence names the one that carries it.
>
> **The tenant is stamped on the request's own span**, not on a child, so "everything this tenant did"
> is a filter rather than a join and costs no extra span. It happens after authentication, because
> the tenant comes from a validated token and from nowhere else.
>
> **`mutationId` gets a span apiece.** A push opens `sync.push`, and each mutation inside it opens
> `sync.push.mutation` carrying the device-minted id, its type, how it was answered and — for a
> refusal — the `ADR-0012` code. A replay answered from the ledger is tagged as a replay and is
> **not** marked as an error: colouring a healthy retry red teaches whoever reads the trace to ignore
> the colour.
>
> The **outbox dispatch** span in §1 is still outstanding: there is no dispatcher to open one
> (slice 3).

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
