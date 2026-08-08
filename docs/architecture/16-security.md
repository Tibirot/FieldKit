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
- **Bypass is banned at compile time:** `IgnoreQueryFilters()` and `ExecuteSqlRaw` are banned symbols
  in every production project (AT-9, [module boundaries §5](10-module-boundaries.md#5-enforcement--architecture-tests)),
  so isolation cannot be switched off — the build fails on the developer's machine rather than in
  review. Test projects are exempt: proving the filter works requires looking past it.
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
- CORS locked to known origins, rate limiting on `/sync` and auth paths.

### 6.1 Content-Security-Policy

Every document response from the front end carries a **strict CSP**
([`lib/security/csp.ts`](../../frontend/lib/security/csp.ts), served from
[`proxy.ts`](../../frontend/proxy.ts)), alongside `X-Content-Type-Options: nosniff` and
`Referrer-Policy: strict-origin-when-cross-origin`.

**It is the control the token decision rests on.** Authorization code + PKCE puts an access token in
the browser ([ADR-0008](adr/0008-identity-and-multi-tenancy.md)) so the field app can refresh after
going offline mid-shift; the trade is that an XSS on this origin reaches those tokens. A CSP is what
makes injected script hard to execute in the first place.

> **This section is here because the claim came first.** `lib/auth/oidc.ts` justified keeping tokens
> in the browser partly on the grounds that the app "ships a strict CSP", and no CSP was ever sent —
> found by an audit reading the code against the docs. A security control that exists only in the
> prose justifying something else is the worst state for one to be in.

`script-src` is `'self'` + a **per-request nonce** + `'strict-dynamic'` — never `'unsafe-inline'`,
which would allow exactly the injected script the tokens need protecting from. `style-src` does
allow `'unsafe-inline'`: Next injects critical CSS inline and does not nonce it, and injected style
cannot execute. `connect-src` names this origin and Keycloak's — the API is same-origin, proxied
under `/api/` — so it also bounds where a successful injection could send anything.

**The cost is that the app renders per request.** A nonce must be fresh per response, and a
prerendered document carries the nonce it was built with; with static rendering the header and the
HTML disagree on every request and every script is refused. Measured, not assumed: a static build
serves **zero** `nonce=` attributes against a header that has one. So the locale layout sets
`dynamic = "force-dynamic"`. That is cheap here — these pages are shells that fetch their data in
the browser after hydrating — and it is the reason to revisit this if a genuinely static, cacheable
page ever matters more than the policy.

Development relaxes two things and only two: `'unsafe-eval'` and `ws:`, because the dev server
compiles with `eval` and talks over a websocket. Both are asserted absent from the production
policy ([`csp.test.ts`](../../frontend/lib/security/csp.test.ts)).

Not covered, deliberately: `/api/*` and static assets are outside the proxy's matcher — a CSP
governs documents, and the API serves JSON. **HSTS is not set yet**; it belongs with the TLS
termination that comes with real deployment, and setting it from the app in a dev environment served
over plain HTTP would be a header nobody could act on.
- Secrets via Aspire/user-secrets in dev and the platform secret store in prod — never in source.
- **Dependency auditing:** `NuGetAudit` runs on every restore (transitive included) and CI surfaces
  advisories; known transitive CVEs are pinned to patched versions with a comment citing the GHSA.
  Every NuGet version — pins included — lives in [`Directory.Packages.props`](../../Directory.Packages.props)
  under central package management, so a pin cannot be applied to one project and missed in another.
  The standing one is `MessagePack` → 2.5.302 (GHSA-hv8m-jj95-wg3x), pinned transitively over
  Aspire's 2.5.192. Making high-severity audit warnings a build error is a considered future gate
  (weighed against lockout when a framework-transitive CVE has no fix yet).
- **npm transitive CVEs** use `overrides` in [`frontend/package.json`](../../frontend/package.json),
  the same idea as the NuGet pins above. `next@16.2.12` declares `postcss` as exactly `8.4.31` and
  `sharp` as `^0.34.5`; **the patched versions are unreachable by resolution**, because an exact pin
  admits nothing else and a caret on a `0.x` line stops before `0.35.0`. Checked 2026-08:
  `next@latest` was itself 16.2.12, so there was no upstream release to wait for.

  | GHSA | Sev | Package | Patched | Override |
  |---|---|---|---|---|
  | [GHSA-r28c-9q8g-f849](https://github.com/advisories/GHSA-r28c-9q8g-f849) | high | postcss | 8.5.18 | `^8.5.18` |
  | [GHSA-6g55-p6wh-862q](https://github.com/advisories/GHSA-6g55-p6wh-862q) | high | postcss | 8.5.12 | `^8.5.18` |
  | [GHSA-qx2v-qp2m-jg93](https://github.com/advisories/GHSA-qx2v-qp2m-jg93) | moderate | postcss | 8.5.10 | `^8.5.18` |
  | [GHSA-f88m-g3jw-g9cj](https://github.com/advisories/GHSA-f88m-g3jw-g9cj) | high | sharp | 0.35.0 | `^0.35.0` |

  An override is a standing deviation from what a maintainer declared, so it is **not** left to rot:
  `frontend/overrides.test.ts` fails the build the moment Next changes either declaration, forcing a
  re-evaluation rather than trusting anyone to notice, and separately asserts that *every* resolved
  copy in the lockfile is patched — a nested copy is how these hid in the first place. Ranges rather
  than exact pins are deliberate: a later regeneration picks up further patches without editing this
  table.

  **Residual risk, sharp.** `0.34 → 0.35` is a minor bump on a `0.x` line, so semver permits breakage,
  and Next loads sharp only when images are actually processed — which, with no `next/image` in the
  app, is never during the build or the test run. The
  guard round-trips an encode/resize/decode to catch a native or ABI break, which is the likely
  failure, but cannot prove every API Next calls is unchanged. The app uses no `next/image` today;
  first real exposure is whenever it does, and that work should re-verify.
- **Dependabot** covers what a manual pin can't: *security* updates open a PR per advisory, and
  *version* updates ([`.github/dependabot.yml`](../../.github/dependabot.yml)) keep npm, NuGet, and
  GitHub Actions current. Minor/patch are grouped into one PR per ecosystem (weekly for npm and
  NuGet, monthly for Actions); **majors arrive individually** because they need real attention. A
  7-day `cooldown` applies to version updates only — the risk automation introduces is adopting a
  freshly published compromised release. Its PRs pass the same required status checks as any other.
- **Held-back majors, and what that costs.** Some updates are blocked in
  [`dependabot.yml`](../../.github/dependabot.yml) because the surrounding toolchain can't accept
  them — currently `eslint` majors (no released `eslint-plugin-react` supports ESLint 10) and
  `typescript` **≥7** (typescript-eslint peers `<6.1.0`; TS 6 is deliberately *not* blocked).
  `@types/node` majors are held permanently because the types track the runtime, not the registry.
  Each entry states its removal condition. **The cost is real and worth stating plainly:** `ignore`
  is consulted for *security* updates too, so an advisory whose only fix ships in a blocked version
  cannot be raised while the entry stands. In-major security patches still flow and all three are
  devDependencies, which bounds it — but "security updates are never delayed" holds only for
  packages with no ignore entry.
- **Secret scanning + push protection** are enabled on the repository. Push protection is the one
  that matters: `never commit secrets` is otherwise a convention, and the commit that breaks it is
  the one thing here that reverting cannot undo — a published credential must be rotated.

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
