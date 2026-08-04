# API Contracts

> **Status:** ✅ Baseline · **Last updated:** 2026-08

The HTTP surface of `FieldKit.Server`: shape, conventions, versioning, the error model,
idempotency, and how the sync endpoints differ from the CRUD ones. Module-internal contracts
(the in-process `I…` interfaces) are covered in [module boundaries](10-module-boundaries.md);
this doc is the **external** API.

## 1. Shape & conventions

- **ASP.NET Core Minimal APIs**, one endpoint group per module, mapped by the module itself
  (`MapOrdersModule`) — the host composes; modules own their routes ([module boundaries](10-module-boundaries.md)).
- Base path `/api`; resources are plural nouns (`/api/outlets`, `/api/orders`). Sync lives under
  `/sync` ([sync engine](12-offline-sync-engine.md)).
- **OpenAPI** generated (`Microsoft.AspNetCore.OpenApi`, already referenced); the spec drives typed
  clients for the Next.js app.
- JSON, camelCase; `Money` serialized as `{ "amount": "12.50", "currency": "EUR" }` (string amount
  to avoid float loss); timestamps ISO-8601 UTC.

## 2. Two API styles

| Style | Used by | Characteristics |
|---|---|---|
| **Resource/CRUD** | Back office (online) | Standard REST; paged lists; optimistic concurrency via ETag/rowversion |
| **Sync (batch)** | Field app | `/sync/pull` (delta) + `/sync/push` (idempotent batch) — coarse-grained, offline-oriented ([sync engine](12-offline-sync-engine.md)) |

The field app almost never calls CRUD endpoints directly — it reads the **local store** and syncs.
This separation is deliberate: the offline path has different needs (batching, idempotency,
deltas) than the back-office path.

## 3. Error model — RFC 7807 Problem Details

All errors return **`application/problem+json`** ([`AddProblemDetails`](../../FieldKit.Server/Program.cs)
already wired), with a stable, machine-readable `code`:

```jsonc
{
  "type": "https://fieldkit/errors/outlet-closed",
  "title": "Outlet is closed",
  "status": 409,
  "code": "OUTLET_CLOSED",
  "detail": "Outlet 0f1c… closed 2026-07-30",
  "traceId": "00-…"                // correlates to the trace (observability)
}
```

- **`code`** is the contract (clients switch on it); `title`/`detail` are human text.
- Validation failures (FluentValidation) → `400` with per-field errors.
- `traceId` ties every error to a distributed trace ([observability](15-observability.md)).

### 3.1 A body the server cannot read

A request body that will not bind is **`400`**, not `500` — including an enum name that is not one of
the names (`{"status":"Nonsense"}`), a body that is not JSON, and a value of the wrong shape. The
`detail` names the offending JSON path (`$.type`), which is the part of the parser's complaint that
is about the caller's request rather than about the server's types.

This needed saying because it did not hold. ASP.NET already decides these are 400s — minimal APIs
raise a `BadHttpRequestException` that carries its own status code — but the bare
`UseExceptionHandler()` reported every unhandled exception as a server fault, status code and all.
The result was an API telling callers their correct payload had broken it. It also raised the wrong
alarm: `5xx` is what pages someone, so a device syncing one bad enum name would have opened an
incident for a client-side typo.

The mapping is deliberately narrow ([`ProblemDetailsExtensions`](../../FieldKit.Server/ProblemDetailsExtensions.cs)):
**only** `BadHttpRequestException` chooses its own status, and it can only ever choose a `4xx`.
Everything else stays a `500`. Widening it further would trade a misreported client error for a
worse one — a genuine server fault reported as the caller's problem is a fault nobody investigates.

## 4. Idempotency

- **Sync push:** every mutation carries a client **`mutationId`**; the server dedupes and returns
  the prior result on replay ([sync engine](12-offline-sync-engine.md)).
- **Unsafe CRUD** (rare, back office): support an `Idempotency-Key` header on non-idempotent POSTs
  where a retry could double-apply.

## 5. Versioning

- **URL-segment versioning** (`/api/v1/…`) introduced when the first breaking change lands; v1 is
  implicit until then.
- The **sync protocol is versioned independently** (a `syncProtocolVersion` in the pull/push
  envelope) because devices in the field may lag the server; the server supports the current and
  previous protocol version.
- Additive changes (new optional fields) are **not** breaking and don't bump the version.

## 6. AuthN/AuthZ on the wire

- **JWT bearer** on every call; tenant + permissions from the token ([ADR-0008](adr/0008-authentication-and-multitenancy.md)).
- **Multi-issuer:** with realm-per-tenant, tokens come from different issuers/JWKS per tenant; the
  API resolves and validates against the right realm's keys per request (cached JWKS) — not a single
  fixed authority ([ADR-0008](adr/0008-authentication-and-multitenancy.md)).
- Endpoints declare required **permissions** (`RequirePermission("order:submit")`); missing →
  `403` with `code: FORBIDDEN`.
- No tenant id is ever accepted from the client body/route — it comes from the token only (a
  crafted `tenantId` cannot cross tenants).

## 7. Pagination, filtering, caching

- Lists: cursor or offset paging with `page`/`pageSize` (or `cursor`); envelopes include
  `total`/`nextCursor`.
- Read-heavy reference endpoints use **Redis output cache** (already wired via Aspire) with short
  TTLs and cache invalidation on the relevant integration events.

## 8. Representative endpoints (illustrative)

| Method | Path | Permission | Notes |
|---|---|---|---|
| `GET` | `/api/outlets?territoryId=&page=` | `outlet:read` | Back office list |
| `POST` | `/api/outlets` | `outlet:write` | Create; `201` + Location |
| `POST` | `/api/products/price-lists/{id}/publish` | `pricing:manage` | Emits `PriceListPublished` |
| `POST` | `/sync/pull` | authenticated | Reference delta by watermark |
| `POST` | `/sync/push` | authenticated | Idempotent mutation batch |
| `POST` | `/sync/photos/presign` | authenticated | Presigned URL for photo upload |
| `GET` | `/health`, `/alive` | — (dev) | Aspire health checks ([observability](15-observability.md)) |
