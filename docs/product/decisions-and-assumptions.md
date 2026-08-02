# Decisions & Assumptions

> **Status:** ✅ Baseline · **Last updated:** 2026-08 · **Owner:** Tiberiu Socea

This document is the resolved-decisions ledger for FieldKit. It closes the open forks that
were blocking a complete picture of the app, and it records the domain mechanics drafted from
domain knowledge. It is the **input** that the module specs, the domain model, and several
ADRs build on.

**Legend**

- **✅ Decided** — a product/scope call made deliberately (during the decision session). Changing it is a real pivot.
- **📝 Assumed** — drafted from domain knowledge to unblock the build. Reasonable to override; each says what it would affect. Look for the `> ASSUMPTION` callouts.

---

## Part A — Confirmed decisions

### A1 · Per-tenant customization: config-driven (moderate)

FieldKit supports meaningful per-tenant customization **without** becoming a metadata engine:

- **Custom fields** on core entities (outlet, product, order, visit) stored in a typed
  **JSONB** column, described by a per-tenant field-definition catalog (name, type,
  validation, required). Rendered dynamically in the UI.
- **Configurable visit workflows** — the sequence of steps in an in-store visit is tenant
  configuration, not code.
- **Configurable survey/audit forms** — question sets are authored per tenant (see A2).
- **Per-tenant theming** — branding via design tokens (ties to A7).

Explicitly **not** in scope: tenant-defined *entities* or tenant-authored logic/scripts. The
schema is fixed; the *fields, forms, workflows, and theme* are configurable.

**Implications:** a dedicated **Configuration module** owns the definitions (field-definition
catalog, visit-workflow, survey/audit forms, perfect-store weights) and ships them to devices as
**snapshot-versioned reference config** via the same change-feed the sync engine uses; JSONB value
columns + a server-side validation layer per owning entity; dynamic form rendering on the Next.js
side. → **new [ADR-0009](../architecture/adr/0009-config-driven-customization.md)**; owning module
in [module boundaries §7](../architecture/10-module-boundaries.md#7-module-registry).

### A2 · Audit / perfect-store: structured checks + share-of-shelf + photo

The Audit module captures **structured** shelf data, not just photos:

- **MSL presence** — for each must-stock SKU: present / absent / out-of-stock.
- **Share-of-shelf** — facings per SKU (and/or brand), yielding a share-of-shelf %.
- **On-shelf price check** — observed shelf price vs. expected, flags mismatches.
- **Photo evidence** — one or more photos per audit section (see B5 for sync).
- **Perfect-store score** — a **weighted score** computed from the above; weights are tenant
  configuration (ties to A1). Availability, visibility (share-of-shelf), and price compliance
  are the default pillars.

No image recognition — the rep enters facings/flags; the platform computes the score.

**Implications:** Audit aggregate with typed measurement lines; a scoring service with
configurable weights; Products dependency for MSL/expected price.

### A3 · Internationalization: full (multi-currency + multi-language UI)

FieldKit is built international from the start:

- **Multi-currency** — a `Money` value object (amount + ISO-4217 currency) is used
  everywhere money appears. **No implicit cross-currency arithmetic**; a price list carries
  its currency and everything derived stays in it. Display formatting is locale-aware.
- **Multi-language UI** — the Next.js app is localized (message catalogs via `next-intl`).
  Launch languages: **English + Romanian** (resolved when i18n landed; low cost to add more).
- **Timezones** — all timestamps stored **UTC**, displayed in the user's timezone; a visit's
  "day" is resolved in the outlet's local timezone.
- **Localized reference data** (e.g. product names per language) — modeled as translation
  tables but treated as **Could-have** (Phase 4), so the core build isn't gated on content.

**Implications:** `Money` in `SharedKernel`; locale/timezone on the user profile; i18n
routing/middleware in Next.js. → **new [ADR-0010](../architecture/adr/README.md)**.

### A4 · Offline data scope: territory-scoped

A rep's device pulls **only what their territory needs**: their assigned outlets, their
journeys, and the products/prices/assortments/planograms relevant to those outlets — not the
whole tenant.

**Implications:** the sync **pull** is parameterized by the rep's territory assignment; the
delta protocol filters by territory; IndexedDB footprint stays small (see B6 sizing). This is
also a nice sync-scoping story for the [sync engine deep dive](../architecture/12-offline-sync-engine.md).

### A5 · Authentication: Keycloak (OIDC) via Aspire, realm-per-tenant

- **Keycloak** is the OIDC identity provider, run as an **Aspire-orchestrated container** in
  dev (and a container app in prod). Free, self-contained, production-realistic.
- **Realm-per-tenant** for clean identity isolation between tenants.
  > ASSUMPTION (📝): realm-per-tenant over single-realm-with-tenant-claim. Realm-per-tenant
  > gives the strongest isolation and per-tenant login theming; the trade-off is more realms
  > to provision (automated via the Keycloak admin API). Override if you'd prefer a single
  > realm with a `tenant` claim + groups for simpler ops.
- The API validates **JWT bearer** tokens; the token carries the tenant and the user's
  permissions, which flow into the `ITenantContext` and authorization checks.

**Implications:** IAM integrates with Keycloak rather than owning passwords; tenant
provisioning includes realm creation. → fills **[ADR-0008](../architecture/adr/0008-authentication-and-multitenancy.md)**.

### A6 · Hosting / live demo: Azure Container Apps via `aspire deploy`

The live demo targets **Azure Container Apps**, published from Aspire. Chosen for the
end-to-end cloud-native story and alignment with the Azure/AKS background on the CV.

> ASSUMPTION (📝) for the backing services in prod: Azure Database for PostgreSQL (flexible
> server), Azure Cache for Redis (or a Redis container app to save cost), Azure Blob Storage
> for photos, Keycloak as a container app. **Scale-to-zero when idle** to keep the demo cheap.
> Override any of these for cost/simplicity.

**Implications:** the AppHost must produce a clean ACA manifest; connection strings and object
storage are provided by managed services in prod, containers in dev. → **new [ADR-0011](../architecture/adr/README.md)**.

### A7 · UI toolkit: shadcn/ui + Tailwind

- **shadcn/ui + Tailwind CSS**, Lucide icons. Copy-in components owned in-repo, themed via
  **design tokens (CSS variables)** — which is also the mechanism for **per-tenant branding**
  (ties to A1).
- **Field app is mobile-first** (in-store, one-handed, offline); **back office is
  desktop-first**. One Next.js app, two responsive experiences.

> Note: this decision picks the *toolkit and design approach*, not the screens. Wireframes of the
> key flows have since been delivered — see [ux/README.md](../ux/README.md) (12 screens +
> interactive Artifact).

### A8 · Device & sync behavior: one active device, auto background sync

- **One active device per rep** for **pull/bind**. Registering a new device deactivates the
  previous one for binding and triggers a fresh full sync on the new device.
- **But a deactivated device may still complete a final "drain" push** of its append-only outbox
  before it is hard-blocked. Because transactional data is device-owned, append-only, and
  idempotent by mutation id, an old-device push cannot cause split-brain — so this closes the
  "lost a day of work when the device was swapped" hole without weakening the model. Only *pull/
  bind* is exclusive to the active device. (See [sync engine §7](../architecture/12-offline-sync-engine.md#7-device-lifecycle).)
- **Sync triggers:** automatically on **reconnect**, plus **periodic background sync** (Background
  Sync API where available), plus a manual **"Sync now."**
- This one-device + device-owned-data model is what lets the conflict story stay simple (B7).

**Implications:** a device registry (Sync module); offline-tolerant token refresh; sync
scheduling in the service worker. → informs **[ADR-0007](../architecture/adr/0007-offline-sync-strategy.md)**.

---

## Part B — Drafted domain mechanics (📝 override-able)

These were the "I draft → you confirm" items. They are my best-judgment defaults; each is
marked so you can strike or change it.

### B1 · Pricing & promotions

> ASSUMPTION. **Price resolution:** a **price list** is assigned per `(tenant, channel)` with
> optional per-outlet override; each product has a price (with currency) within a price list.
> **Promotion types:** percentage-off, fixed-amount-off, volume/tiered (buy N+ → discount),
> and BOGO/bundle (buy X get Y). **Stacking:** at most one line-level promotion per order line,
> chosen by configurable **priority**; order-level promotions are separate and additive.
> **Tax:** VAT via a per-product tax class × tenant/country rate; prices stored **net**, tax
> computed at order time. **Order minimums:** optional minimum order value per channel/outlet.

Affects: Products & Pricing, Order. This is the most rules-heavy area — worth your review.

### B2 · Assortment & must-stock list (MSL)

> ASSUMPTION. **Assortment** is defined per **channel** with per-outlet overrides; the **MSL**
> is the subset of the assortment flagged must-stock. Assortment drives the order
> suggested-list; MSL drives the audit availability checks.

Affects: Products, Outlets, Order, Audit.

### B3 · Journey generation

> ASSUMPTION. Each outlet has a **call frequency** (visits per cycle over a cycle length, e.g.
> 1×/week). The generator distributes an outlet's required visits across the rep's **working
> calendar** honoring frequency and territory. Reps **may add unplanned visits**; may **not
> delete** a planned visit but can mark it **not-visited with a reason**; **rescheduling within
> the cycle** is allowed.

Affects: Journey, Visit, Organization.

### B4 · Order lifecycle

> ASSUMPTION. States: **Draft** (on device) → **Submitted** (synced) → **Accepted** *or*
> **Rejected** (server) → **Cancelled**. Editable while **Draft**; **locked after submit** (this
> deliberate rule is what keeps sync conflict-free — see B7). **Returns out of scope for v1.**
>
> **Rejection remediation (resolves finding S1).** A server **rejection is whole-order** (with a
> reason code + the offending line, e.g. `SKU_OFF_ASSORTMENT`, `OUTLET_CLOSED`). A rejected order
> **re-opens into an editable state on the device** — a controlled, documented exception to the
> lock — so the rep fixes the flagged line(s) and **resubmits under a new mutation id**. This keeps
> the append-only/idempotent push intact (the original submission's mutation id is terminal; the
> correction is a new one) while guaranteeing rejected work is never stranded. Without this, the
> lock would make a rejected order unfixable. See [Order §5 BR-ORD-9](../product/23-order-capture.md#5-business-rules)
> and [sync engine §4](../architecture/12-offline-sync-engine.md#4-push-protocol-device-owned-mutations).

Affects: Order, Sync.

### B5 · Photo / binary sync

> ASSUMPTION. Photos are **downscaled on-device** (max ~1600px, JPEG ~0.7), stored as blobs in
> IndexedDB, and uploaded to object storage on reconnect via **presigned URLs**, **separately**
> from the JSON push and retried independently. The audit record references the object key.

Affects: Audit, Sync, hosting (object storage).

### B6 · Scale assumptions (representative, not limits)

> ASSUMPTION. Up to ~20 tenants; per tenant up to ~5,000 outlets, ~500 SKUs, ~200 reps,
> ~25 visits/rep/day. A rep's **territory** ≈ 150–300 outlets + ~500 SKUs ⇒ a **few MB** on
> device — comfortably within IndexedDB. These numbers guide indexing and payload design; they
> are not hard caps.

Affects: data/persistence, sync payload design.

### B7 · Conflict-resolution matrix

> ASSUMPTION. Conflicts are avoided **by design** via server-authoritative reference data +
> device-owned append-only transactional data + locked-after-submit orders (B4) + one active
> device (A8):

| Data class | Examples | Direction | Conflict policy |
|---|---|---|---|
| Reference / master data | products, prices, outlets, journeys, assortments, planograms | server → device (read-only on device) | **Server authoritative.** Device always takes the latest server version on pull. No conflict. |
| Device-owned transactional | visits, audits, orders | device → server (append) | **Idempotent append**, keyed by client-generated mutation id. The device owns the record; no competing writer. |
| Back-office edits during offline window | admin changes master data while rep is offline | server → device on next pull | Server wins; rep's already-captured transactions remain valid, tagged with the **snapshot version** they were captured against (kept for audit). |

The only genuinely hard case (concurrent edits to the same mutable record from two writers) is
**engineered out** rather than solved with vector clocks/CRDTs — a deliberate, documented
simplification appropriate to the domain. Deep dive: [sync engine](../architecture/12-offline-sync-engine.md).

### B8 · Privacy / GDPR posture

> ASSUMPTION. Personal data present: rep identity, rep **geolocation at check-in** (a single
> point, **not** continuous tracking), and outlet contact persons. Posture: strict tenant
> isolation, geolocation captured only at visit check-in, per-tenant retention policy, and
> right-to-erasure handled at the IAM level. Light but explicit. Deep dive: [security](../architecture/16-security.md).

---

## Part C — ADRs triggered by these decisions

All are now written and **Accepted** — see the [ADR index](../architecture/adr/README.md).

| ADR | Title | Trigger | Status |
|---|---|---|---|
| 0003 | Adopt .NET Aspire | +ACA deploy target (A6) | Accepted |
| 0004 | Next.js offline-first front end | +shadcn/Tailwind, PWA, i18n routing (A7, A3) | Accepted |
| 0007 | Offline sync strategy | territory scope (A4), one-device (A8), conflict matrix (B7) | Accepted |
| 0008 | Authentication & multi-tenancy | Keycloak realm-per-tenant (A5) | Accepted |
| 0009 | Config-driven customization model | A1 | Accepted |
| 0010 | Internationalization (currency & language) | A3 | Accepted |
| 0011 | Deployment target: Azure Container Apps | A6 | Accepted |

## Part D — Still genuinely open

Small, non-blocking; will resolve as the specs are written:

- Keycloak realm-per-tenant vs. single-realm (A5) — assumption stands; bounded to ~20 tenants
  (see [ADR-0008](../architecture/adr/0008-authentication-and-multitenancy.md); revisit toward
  single-realm **past ~50 tenants**).
- Prod backing-service specifics on Azure (A6) — assumption stands; finalize at deploy time.

*(Wireframes, once open here, are now delivered — [ux/README.md](../ux/README.md). The second UI
language, also once open here, resolved to **Romanian** when the i18n scaffold landed — see
[ADR-0010](../architecture/adr/0010-internationalization.md).)*
