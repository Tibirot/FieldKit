# Keycloak dev realm

`fieldkit-dev-realm.json` is imported into the Aspire-orchestrated Keycloak container on every
start. JSON has no comments, so the reasoning lives here.

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

Three protocol mappers on the `fieldkit-web` client, each load-bearing for the API:

| Claim | Mapper | Why |
|---|---|---|
| `tenant` | hardcoded | The FieldKit `TenantId` for this realm. Hardcoded *per realm* — that is what makes realm-per-tenant resolve to a tenant id. Matches the id `DevTenantContext` used, so the swap in the token-derived context changes no existing data. |
| `permissions` | realm roles, multivalued | Realm roles **are** the permission strings (`resource:action`), flattened into one claim so the API reads permissions without knowing about roles (BR-IAM-2). |
| `audience` | hardcoded `fieldkit-api` | Keycloak's default access-token audience is `account`. Without this the API cannot validate `aud`, and a token minted for any client would be accepted. |
