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

### A refused write

**One envelope, whatever the status** — `400`, `409`, `415`. A client reads every refusal the same
way rather than sniffing between shapes:

```jsonc
{
  "errors": [
    { "field": "code",                      "message": "An outlet with code 'OUT-1' already exists." },
    { "field": "customFields.chiller_count", "message": "'chiller_count' must be at most 50." },
    { "field": null,                        "message": "The file has a header but no rows." }
  ]
}
```

- **`field` is the JSON path the caller sent** — `code`, `channelId`,
  `customFields.chiller_count`. Not a column, not a form control: the API can only promise something
  about the request it received. A form maps it to its own naming, which is one line when the two
  agree and one prefix when they do not.
- **`null` when the problem is about the request as a whole**, so a form shows it at the top rather
  than highlighting a control at random.
- **Every problem at once**, not the first. Someone filling a form wants to fix everything in one
  pass; returning one at a time turns a six-field form into six round trips.

This replaced prose — `{ "error": "A territory needs a name." }` — which reads perfectly and tells a
form nothing about *where* to put it. A screen could only list sentences above a page of inputs, or
re-declare the rules client-side to produce its own field keys, which is a second copy of what the
server owns. The bulk import had already answered this way (`{ row, column, message }`); this is the
same idea for a request with no rows.

#### `message` is English, and that is a known gap

**`message` is the only thing an error carries today, and it is always English** — so a user on
`/ro` gets an English message under a Romanian form control. That is the current behaviour, not an
oversight in this document.

[ADR-0012](adr/0012-server-message-localization.md) decides the fix: refusals gain a stable
`code` and named `args`, resolved client-side through the existing `next-intl` catalogs, with
`message` demoted to an English fallback.

```jsonc
// The target shape. NOT YET IMPLEMENTED — no endpoint emits `code` or `args` today.
{ "field": "name", "code": "channel.name.taken",
  "args": { "name": "Modern Trade" },
  "message": "A channel named 'Modern Trade' already exists." }
```

The change is additive: `field` and `message` keep their meaning, and a client that ignores `code`
behaves exactly as it does now. This section will describe `code` as shipped once modules begin
emitting it — see the ADR for the staging.

### Unhandled failures

Everything not deliberately refused returns **`application/problem+json`**
([`AddProblemDetails`](../../FieldKit.Server/Program.cs)), with `traceId` tying it to a distributed
trace ([observability](15-observability.md)).

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

A list endpoint returns an **envelope**, not a bare array:

```jsonc
{
  "items": [ /* … */ ],
  "total": 812,      // of the *filtered* set, so "1–50 of 812" is true
  "page": 1,         // echoed, so a clamped request is visible
  "pageSize": 50
}
```

Driven by query parameters carrying the same names back — `?page=&pageSize=&search=&sort=&descending=`
plus whatever filters the resource owns. A request and its response describing one thing two ways is
a small cost every client pays forever.

**Offset, not keyset.** A back office browsing wants a total and the ability to jump to page 40; a
device replicating a dataset wants stability under concurrent writes and constant cost at depth.
Different problems — Sync keeps its own cursor feed (`rowVersion > cursor`,
[sync engine §4](12-offline-sync-engine.md)) for the second, and forcing one mechanism onto both is
the mistake rather than having two. At master-data scale offset's weakness never bites: skipping
4,800 rows is a scan Postgres does in under a millisecond.

Rules that make it correct rather than merely present:

- **The sort always ends on a unique column.** Rows with equal sort keys have no defined order in
  SQL, so without a tiebreak Postgres may order them differently between the query for page 1 and the
  query for page 2 — a row appears on both while another appears on neither. Sorting by a
  low-cardinality column like status is exactly where that bites.
- **Sort is a closed enum, never a column name from the query string** — the alternative is an
  injection surface, or an `ORDER BY` over a column with no index.
- **The total is counted on the filtered set, before the page is taken.** Counted before filtering,
  the pager offers pages that are always empty.
- **Search text is escaped.** `%` and `_` are `LIKE` wildcards: unescaped, a search for `50%`
  matches everything beginning "50", and a lone `%` matches the whole table while looking like a
  search that found a lot.
- **Nonsense is clamped, not refused.** `pageSize=100000` and `page=-2` resolve to the cap and the
  first page, and the echoed values say so. Nobody types those on purpose, and answering an obvious
  typo with a 400 is worse than answering it with a page.
- **Only what a module owns can be sorted or filtered.** An outlet's territory comes from
  Organization *after* the page is fetched (`ORG-05`), so the database cannot order by it — sorting
  on it would order the fifty rows already chosen, which is the page and not the list.
- Read-heavy reference endpoints use **Redis output cache** (already wired via Aspire) with short
  TTLs and cache invalidation on the relevant integration events.

## 8. Representative endpoints (illustrative)

| Method | Path | Permission | Notes |
|---|---|---|---|
| `GET` | `/api/outlets?search=&channelId=&status=&sort=&page=` | `outlet:read` | Back office list; paged envelope |
| `POST` | `/api/outlets` | `outlet:write` | Create; `201` + Location |
| `POST` | `/api/products/price-lists/{id}/publish` | `pricing:manage` | Emits `PriceListPublished` |
| `POST` | `/sync/pull` | authenticated | Reference delta by watermark |
| `POST` | `/sync/push` | authenticated | Idempotent mutation batch |
| `POST` | `/sync/photos/presign` | authenticated | Presigned URL for photo upload |
| `GET` | `/health`, `/alive` | — (dev) | Aspire health checks ([observability](15-observability.md)) |
