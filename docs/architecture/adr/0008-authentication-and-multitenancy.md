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

**Landed.** Keycloak runs as an Aspire container with the dev tenant realms imported from source, and
the API validates the JWT bearer — signature, issuer, audience and lifetime — against **whichever
realm minted it**. The token carries `tenant`, `sub` and a flattened `permissions` claim, verified
end to end against a real Keycloak in the integration tests.

**Multi-issuer validation** (finding S6) is wired: issuer and signing keys are resolved per request
from a registry backed by the tenant table, with per-realm JWKS caching. Three properties follow, and
each has a test that can fail because a second realm now exists:

- **The tenant table is the trust list.** A realm no tenant row claims yields no issuer and no signing
  keys, so someone who can create a realm on the identity provider cannot thereby create a tenant.
- **A token's `tenant` claim must match the tenant that owns its issuer.** Issuer validation proves a
  token came from a realm we trust and says nothing about who it claims to be; without this
  comparison a trusted realm could mint tokens for *any* tenant and the query filter would honour
  them. The second dev realm carries a client that does exactly that, so the check is exercised
  against a real signature rather than a mangled one.
- **A suspended tenant loses access at validation**, not at first query.

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

**Front-end sign-in** (`IAM-01`) is authorization-code + PKCE from the browser, against the realm of
the workspace the user names. Three consequences are worth recording, because each was a choice:

- **Tokens live on the device, not in a server-side session.** That is what makes going offline
  mid-shift survivable ([IAM §7](../../product/10-identity-and-access.md#7-offline-behavior)) — a
  cookie session cannot restore anything once the server is unreachable. The cost is that an XSS on
  this origin reaches the tokens, which is why the app loads no third-party script.
- **The realm is named by the user, once.** Realm-per-tenant leaves nothing to redirect an unknown
  visitor to. A subdomain needs wildcard DNS and per-tenant redirect URIs; an email-domain lookup
  needs a public endpoint that confirms whether a tenant exists. Asking is the reversible option, and
  the mapping lives behind one function for when provisioning (`IAM-10`) picks a convention.
- **Keycloak's address is read per request, never baked into the bundle.** It is assigned per run in
  dev and per environment in production, and a stale one is not a broken link — it mints tokens whose
  issuer the API refuses, which presents as signing in successfully and staying signed out.

**Not yet, and deliberately so:**

- **No realm provisioning** (`IAM-10`). The dev realms are hand-written, and the tenant rows that make
  them trusted issuers come from configuration (`Iam:SeedTenants`) rather than from provisioning. A
  seeded id that disagrees with its realm's hardcoded `tenant` claim produces tokens that
  authenticate and are then refused — the binding above working, for the wrong reason.
- **No account provisioning.** IAM owns the FieldKit *profile*; creating the matching Keycloak
  account (spec F2) and the tenant realm (`IAM-10`) is Phase 2. Doing it now would put Keycloak
  admin credentials into the request path — a blast radius that deserves its own decision rather
  than arriving as a side effect of users CRUD. Until then an operator creates the account and the
  profile links to it by `sub`.
