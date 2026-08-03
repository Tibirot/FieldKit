# ADR-0008: Authentication & multi-tenancy

- **Status:** Accepted
- **Date:** 2026-08
- **Deciders:** Tiberiu Socea
- **Related:** [IAM spec](../../product/10-identity-and-access.md), [security](../16-security.md),
  [data & persistence](../14-data-and-persistence.md),
  decision [A5](../../product/decisions-and-assumptions.md#a5--authentication-keycloak-oidc-via-aspire-realm-per-tenant)

## Context

FieldKit is a **multi-tenant SaaS**: each customer (brand/distributor) is an isolated tenant.
Two questions must be answered on **every request** — *who is this user* (authentication) and
*which tenant's data may they touch* (isolation) — and both must be impossible to get wrong,
including under a crafted/hostile request.

## Decision

### Authentication — Keycloak (OIDC), realm-per-tenant
- **Keycloak** is the identity provider, run as an Aspire-orchestrated container
  ([ADR-0003](0003-adopt-dotnet-aspire.md)). FieldKit does **not** store passwords.
- **Realm-per-tenant** for strong identity isolation and per-tenant login theming.
  > ASSUMPTION (📝): realm-per-tenant over single-realm-with-claim. Stronger isolation, at the
  > cost of realm provisioning (automated via Keycloak admin API on tenant creation). Override to
  > single-realm + `tenant` claim if ops simplicity is preferred.
  >
  > **Bounded to scale (finding S6):** this is acceptable *because* FieldKit targets **≤ ~20
  > tenants** ([B6](../../product/decisions-and-assumptions.md#b6--scale-assumptions-representative-not-limits)).
  > Keycloak degrades (memory/startup/admin) at **hundreds** of realms — its own guidance steers
  > large multi-tenant setups to single-realm + Organizations/groups. Past ~50 tenants this decision
  > should be revisited toward single-realm. It is **not** a general recommendation.
- Front end uses **OIDC authorization-code + PKCE**; the API validates a **JWT bearer** on every
  call. The token carries `tenant`, `sub`, and the user's **permissions**.
- **Multi-issuer validation (finding S6):** realm-per-tenant means each tenant's tokens come from a
  **different issuer / JWKS endpoint**. The API resolves the issuer per request (from the `tenant`/
  `iss`) and validates against that realm's keys — a per-tenant issuer registry with cached JWKS,
  not a single fixed authority. (A single-realm override collapses this to one issuer.)

### Multi-tenancy — TenantId + global query filter
- Every tenant-owned row carries a **`TenantId`** column ([data & persistence](../14-data-and-persistence.md)).
- Tenant is resolved from the token into an ambient **`ITenantContext`** at the start of the
  request.
- EF Core applies a **global query filter** (`WHERE TenantId = @current`) to every tenant-owned
  entity, and a save interceptor **stamps `TenantId`** on insert — isolation is automatic and
  centrally enforced, not per-query discipline.
- Tenancy is **row-level within shared schemas** — *not* schema-per-tenant — keeping migrations
  and operations sane at scale ([ADR-0005](0005-postgres-schema-per-module.md)).

### Authorization — permission-based
- Modules check **permissions** (`order:submit`), never role names
  ([IAM spec](../../product/10-identity-and-access.md)); roles are just permission bundles.

## Options considered

| Concern | Chosen | Rejected |
|---|---|---|
| IdP | Keycloak (self-hosted, OIDC) | ASP.NET Core Identity (mixes auth into monolith); cloud IdP (needs account, less self-contained) |
| Tenant identity | Realm-per-tenant | Single realm + claim (weaker isolation) — kept as an override |
| Data isolation | `TenantId` + global query filter | Schema-per-tenant / DB-per-tenant (migration & ops explosion) |
| AuthZ | Permission-based | Role-name checks (brittle, uncustomizable) |

## Consequences

**Positive**
- Isolation is **central and default** — a forgotten `WHERE TenantId` can't leak data; the filter
  is applied by the ORM, not the developer.
- No password handling in FieldKit; standard, battle-tested OIDC.
- Permission model supports per-tenant role customization ([A1](../../product/decisions-and-assumptions.md#a1--per-tenant-customization-config-driven-moderate)).

**Negative / costs**
- **Query-filter bypass risk:** raw SQL or `IgnoreQueryFilters()` sidesteps isolation — so those are
  **banned symbols in every production project** (AT-9), failing the build rather than review.
- Realm-per-tenant means realm provisioning automation and more Keycloak objects to manage.
- Cross-tenant admin/reporting (platform-level) needs an explicit, audited elevation path — out of
  scope for v1.

**Enforcement:** the tenant-isolation guarantees, the bypass ban, and the threat model live in
[security](../16-security.md); tests assert every tenant-owned entity has the filter and no
`IgnoreQueryFilters` appears in module code.

## Implementation status *(Phase 0)*

The decision above is accepted in full; this records how much of it currently exists, because the
gap is security-relevant and easy to misread as "auth is done".

**Landed.** Keycloak runs as an Aspire container with the dev tenant realm imported from source, and
the API validates the JWT bearer — signature, issuer, audience and lifetime — against that realm.
The token carries `tenant`, `sub` and a flattened `permissions` claim, verified end to end against a
real Keycloak in the integration tests.

`ITenantContext` is **derived from the token** (`IAM-02`): the tenant comes from the `tenant` claim
and nowhere else — not a header, not a route value, not the body. Business endpoints require a
permission (`IAM-05`), checked as `resource:action` strings rather than role names (BR-IAM-2).

Three properties are worth stating because each closes a specific hole:

- **A token without a usable `tenant` claim is rejected at validation**, not at first use. A token
  that authenticates but cannot be attributed is more dangerous than an anonymous one — the request
  would reach the data layer, where the query filter compares against *some* tenant, so the failure
  mode is not "denied" but "attributed to the wrong tenant".
- **The tenant is unreachable from the request.** The previous stand-in honoured an `X-Tenant-Id`
  header; that is now a test asserting a crafted header cannot move a write between tenants.
- **A missing permission is 403, not 401.** Telling a rep with the wrong role to authenticate again
  is a dead end for them and a support ticket for someone else.

**Not yet, and deliberately so:**

- **Single issuer.** Realm-per-tenant means per-tenant issuers and JWKS endpoints (finding S6 above),
  so the finished system resolves the issuer per request against a registry of tenant realms. That
  registry needs a source of tenants, which IAM owns and has not delivered — building it now would
  mean inventing a tenant list to drive it. The API validates the one realm that exists.
- **No realm provisioning** (`IAM-10`). The dev realm is hand-written.
- **No account provisioning.** IAM owns the FieldKit *profile*; creating the matching Keycloak
  account (spec F2) and the tenant realm (`IAM-10`) is Phase 2. Doing it now would put Keycloak
  admin credentials into the request path — a blast radius that deserves its own decision rather
  than arriving as a side effect of users CRUD. Until then an operator creates the account and the
  profile links to it by `sub`.
