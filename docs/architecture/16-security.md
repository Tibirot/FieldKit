# Security

> **Status:** ✅ Baseline · **Last updated:** 2026-08
> **Decisions:** [ADR-0008](adr/0008-authentication-and-multitenancy.md) · privacy [B8](../product/decisions-and-assumptions.md#b8--privacy--gdpr-posture)

Security posture for a multi-tenant SFA platform handling personal data across the US and EU. The
headline risk is **cross-tenant data leakage**; the headline personal-data concern is **rep
geolocation**. This doc states the model and a lightweight threat model.

## 1. Authentication

- **OIDC via Keycloak**, realm-per-tenant; **auth-code + PKCE** on the client; **JWT bearer**
  validated on every API call ([ADR-0008](adr/0008-authentication-and-multitenancy.md)).
- Short-lived access tokens + refresh tokens (offline-tolerant refresh for the field app). FieldKit
  **stores no passwords**.

## 2. Authorization

- **Permission-based** (`resource:action`), checked in module handlers; roles are permission
  bundles ([IAM](../product/10-identity-and-access.md)). No role-name checks.
- Endpoints declare required permissions; failures return `403 / FORBIDDEN`
  ([API contracts](13-api-contracts.md)).

## 3. Tenant isolation (the load-bearing control)

- `TenantId` on every tenant-owned row; EF Core **global query filter** + insert **stamping**
  make isolation automatic ([data & persistence](14-data-and-persistence.md)).
- **Tenant is taken only from the token** — never from client body/route. A crafted `tenantId`
  cannot cross tenants.
- **Bypass is banned:** `IgnoreQueryFilters()` and filter-evading raw SQL fail an
  [architecture test](17-testing-strategy.md) — isolation can't be silently switched off.
- Defence in depth: per-module DB roles scoped to their schema ([ADR-0005](adr/0005-postgres-schema-per-module.md)).

## 4. Data protection & privacy (GDPR)

Per [B8](../product/decisions-and-assumptions.md#b8--privacy--gdpr-posture):

- **Personal data:** rep identity, **check-in geolocation (a single point, not continuous
  tracking)**, outlet contacts.
- **Minimization:** location captured only at visit check-in; no background location trail.
- **In transit / at rest:** TLS everywhere; managed-Postgres/Blob encryption at rest
  ([ADR-0011](adr/0011-deployment-azure-container-apps.md)).
- **Right to erasure:** an IAM-level workflow ([IAM-09](../product/10-identity-and-access.md#6-requirements))
  removes/anonymizes a *user's* personal data while preserving aggregate business records.
- **Tenant offboarding:** a tenant exit produces a **data export** (their master + transactional
  data) and then a **purge** of tenant-owned rows across all schemas (the `TenantId` filter makes the
  scope exact) and the tenant's Keycloak realm. Distinct from user erasure — this is the *tenant*
  leaving.
- **Retention:** per-tenant retention policy for visit/audit history.
- **Photos** live in object storage via short-lived **presigned URLs**, not public buckets
  ([sync engine](12-offline-sync-engine.md)).
- **Accessibility:** the field app targets **WCAG 2.2 AA** — genuinely earned by a one-handed,
  gloved, bright-sunlight in-store context (contrast, touch-target size, no color-only state).

## 5. Device & offline security

- **One active device per rep** for pull/bind; rebinding deactivates the prior device for binding
  (`DEVICE_INACTIVE`) — limits blast radius of a lost device. Deactivation has **two modes**:
  **swap** allows the prior device **one final, time-bounded drain-push** of its append-only outbox
  (safe by idempotency; no split-brain) so a replaced device never loses captured work;
  **compromised** (lost/stolen) **blocks the drain too**, so a suspect device cannot push fabricated
  visits/orders. Admin chooses the mode ([ADR-0007](adr/0007-offline-sync-strategy.md), [sync engine §7](12-offline-sync-engine.md#7-device-lifecycle)).
- On-device data is **territory-scoped** ([A4](../product/decisions-and-assumptions.md#a4--offline-data-scope-territory-scoped))
  — a compromised device exposes one rep's territory, not the tenant.
- IndexedDB is same-origin; sensitive tokens kept in memory/secure storage, not plain
  localStorage.

## 6. Application security baseline

- Validation on all inputs (FluentValidation); custom-field values validated against the
  definition catalog ([ADR-0009](adr/0009-config-driven-customization.md)).
- Parameterized queries / EF Core (no string SQL); output encoding in React by default.
- Security headers, CORS locked to known origins, rate limiting on `/sync` and auth paths.
- Secrets via Aspire/user-secrets in dev and the platform secret store in prod — never in source.
- **Dependency auditing:** `NuGetAudit` runs on every restore (transitive included) and CI surfaces
  advisories; known transitive CVEs are pinned to patched versions with a comment citing the GHSA
  (e.g. `Microsoft.OpenApi` → 2.7.5, `MessagePack` → 2.5.302). Making high-severity audit warnings a
  build error is a considered future gate (weighed against lockout when a framework-transitive CVE
  has no fix yet).
- **Dependabot** covers what a manual pin can't: *security* updates open a PR per advisory, and
  *version* updates ([`.github/dependabot.yml`](../../.github/dependabot.yml)) keep npm, NuGet, and
  GitHub Actions current — grouped to one PR per ecosystem per week, because a dozen ignored PRs is
  not a security control. Its PRs pass the same required checks as any other.
- **Secret scanning + push protection** are enabled on the repository. Push protection is the one
  that matters: `never commit secrets` is otherwise a convention, and the commit that breaks it is
  the one thing here that cannot be undone by reverting — a published credential must be rotated.
  Bot-authored PRs (Dependabot) are outside the agent pre-PR review rule
  ([PR rules §8](../engineering/pull-requests.md#8-agent-rules-imperative--an-agent-must-follow-these));
  CI and human review are their gate.

## 7. Threat model (STRIDE-lite)

| Threat | Vector | Mitigation |
|---|---|---|
| **Cross-tenant read/write** | Crafted ids / tenant in payload | Token-only tenant + global query filter + bypass ban (§3) |
| **Spoofing** | Stolen token | Short TTL + refresh revocation; device deactivation |
| **Tampering** | Replayed/duplicated sync push | Idempotency ledger; server re-validates via contracts ([sync engine](12-offline-sync-engine.md)) |
| **Repudiation** | "I didn't submit that" | Audit stamping (actor + time) + append-only transactional data ([data](14-data-and-persistence.md)) |
| **Info disclosure** | Lost device | Territory-scoped local data; device deactivation; encrypted transport/at-rest |
| **Elevation** | Guessing permissions | Server-side permission checks; deny-by-default |
| **DoS** | Sync flooding | Rate limiting; batch-size limits; scale-to-zero autoscale |

## 8. Out of scope (v1, stated honestly)

Cross-tenant platform-admin tooling, formal pen-test, SSO/SCIM provisioning, and field-level
encryption beyond transport/at-rest. Revisitable; called out so the posture isn't overstated.
