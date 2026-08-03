# Keycloak dev realms

Every `*-realm.json` here is imported into the Aspire-orchestrated Keycloak container on every start,
and the Server tests import the same files. JSON has no comments, so the reasoning lives here.

| Realm | Tenant id | Exists to prove |
|---|---|---|
| `fieldkit-dev` | `…0001` | the ordinary path: authentication, permissions, 401 vs 403 |
| `fieldkit-dev-b` | `…0002` | **multi-issuer validation** — a second issuer, a second JWKS, a second tenant |

`rep-b` holds `role:read` on top of the product permissions, which the first realm's `rep` does not.
That asymmetry is deliberate: it is how "each tenant gets its **own** seeded roles" (`IAM-06`) becomes
provable rather than assumed — without it, the second tenant cannot be asked what roles it has, and a
single shared set would look identical to two correct ones.

## Why there are two realms

With one realm every assertion about issuer resolution passes whether the issuer is resolved per
request or hard-coded, which is exactly why that gap survived as long as it did. Two realms make the
difference observable: each token validates against its own realm's keys, each resolves to its own
tenant, and "tenant isolation" becomes a claim provable over HTTP with two real tokens rather than
one asserted at the `DbContext` with two fabricated tenant contexts.

A realm is only trusted if a **tenant row claims it** — the tenant table is the trust list. Those rows
come from `Iam:SeedTenants` in configuration until provisioning lands (`IAM-10`), and each seeded id
**must match the hardcoded `tenant` claim in the matching realm file**. They are two halves of one
fact: the realm asserts which tenant its tokens belong to, and the row is what makes the API agree.

## The impostor client, which exists to be refused

`fieldkit-dev-b` carries a second client, `fieldkit-impostor`, identical to `fieldkit-web` except
that its hardcoded `tenant` claim names the **first** tenant. Its tokens are properly signed by a
trusted issuer and assert a tenant that issuer does not own.

It is there because the check standing between that token and a complete view of the other tenant's
data — the token's `tenant` claim must match the tenant that owns its issuer — otherwise has no test
that can fail. Editing a real token's payload does not work: the signature breaks first and the
request is refused for the wrong reason, so the test would pass with the check deleted.

Never enable a client like this in a real realm. It is safe here only because this realm is a dev
fixture and the API rejects what it mints.

## Why a realm is committed at all

[ADR-0008](../../docs/architecture/adr/0008-authentication-and-multitenancy.md) makes **a tenant a
Keycloak realm** (realm-per-tenant). Without an imported realm there is no tenant, so the container
would boot into an empty Keycloak that every developer would have to click through by hand — not
reproducible, not demoable, and not reviewable. Committing the realm makes the dev tenant *code*.

Realm **provisioning** for real tenants is automated through the Keycloak admin API when IAM lands
(`IAM-10`, Phase 2). This file is only the dev tenant.

## Three users, because one cannot prove authorization works

| User | Realm roles | Exists to prove |
|---|---|---|
| `rep` | `product:read`, `product:write` | the permitted path succeeds |
| `viewer` | `product:read`, `role:read` | a missing permission is **403**, not 401 — and that read and write are genuinely separate capabilities |
| `admin` | `role:read`, `role:write`, `user:read`, `user:write` | permissions are **independent, not hierarchical** — an admin who can manage roles cannot touch products, and `rep` cannot touch roles |

A single all-powerful user makes an authorization test vacuous: everything passes whether or not the
check is wired up. The differences between these three are the assertions.

`admin` deliberately holds **no** product permissions. That disjointness is what demonstrates
permission-based authorization rather than tiers — there is no "administrator" who implicitly
outranks everyone; there are only capabilities, and you hold them or you do not.

Realm roles here mirror the permission catalogue the modules contribute in code. Keeping them in step
is manual today; the catalogue endpoint (`GET /api/iam/permissions`) is what an admin UI will read,
and realm provisioning (`IAM-10`) is what will eventually generate these.

## The credentials in this file are not secrets

`dev-only-not-a-secret` is a fixture password for a container that listens on localhost with
`sslRequired: none`. It guards nothing and is deliberately named so it cannot be mistaken for a real
credential or copied into an environment where it would matter.

What is **not** here, and must never be: the Keycloak **admin** credentials. Those are Aspire
parameters, generated per developer and stored in user-secrets — never in source. See
[security §6](../../docs/architecture/16-security.md#6-application-security-baseline).

## No data volume, by design

The Keycloak resource deliberately has **no** `WithDataVolume()`, unlike Postgres.

Keycloak's `--import-realm` **skips a realm that already exists**. With a persistent volume, editing
this file would appear to do nothing on the next run — you would be looking at the realm imported
weeks ago, and the only fix is knowing to delete the volume. That trap costs more than the state is
worth: this realm is regenerated from source in seconds, and anything hand-created in the admin
console is meant to be throwaway.

Postgres keeps its volume because the data there is *not* reproducible from source.

## Claims the token carries

Three protocol mappers on each realm's `fieldkit-web` client, each load-bearing for the API:

The `redirectUris` and `webOrigins` on that client are the front end's sign-in contract: the browser
is sent back to `/{locale}/auth/callback` on the app's own origin, and Keycloak refuses any redirect
target not listed. Change where the app is served from and these move with it — otherwise sign-in
fails at Keycloak, before the app gets a chance to say anything.

| Claim | Mapper | Why |
|---|---|---|
| `tenant` | hardcoded | The FieldKit `TenantId` for this realm. Hardcoded *per realm* — that is what makes realm-per-tenant resolve to a tenant id. Matches the id `DevTenantContext` used, so the swap in the token-derived context changes no existing data. |
| `permissions` | realm roles, multivalued | Realm roles **are** the permission strings (`resource:action`), flattened into one claim so the API reads permissions without knowing about roles (BR-IAM-2). |
| `audience` | hardcoded `fieldkit-api` | Keycloak's default access-token audience is `account`. Without this the API cannot validate `aud`, and a token minted for any client would be accepted. |
