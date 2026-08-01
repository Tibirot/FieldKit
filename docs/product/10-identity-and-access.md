# Functional Spec — Identity & Access (IAM)

> **Module:** IAM · **Group:** Admin · **Phase:** 1 · **Status:** ✅ Baseline
> **Depends on:** — (foundational) · **Consumed by:** every module (tenant + permission context)

## 1. Purpose

IAM is the foundation every other module stands on. It answers three questions for every
request: **which tenant**, **which user**, and **what are they allowed to do**. Authentication
is delegated to **Keycloak** (OIDC — see [decision A5](decisions-and-assumptions.md#a5--authentication-keycloak-oidc-via-aspire-realm-per-tenant));
IAM owns the *authorization* model and the FieldKit-side user profile.

## 2. Actors

| Actor | Interest |
|---|---|
| Tenant Administrator (Victor) | Provision users, assign roles, manage tenant settings |
| Sales Ops / Admin (Elena) | Uses roles/permissions granted by the admin |
| Every user | Logs in, carries a permission set into every screen and API call |
| The platform itself | Resolves tenant + permissions on each request |

## 3. Core concepts

- **Tenant** — an isolated customer (brand/distributor). Backed by a Keycloak **realm**
  (realm-per-tenant, [A5](decisions-and-assumptions.md#a5--authentication-keycloak-oidc-via-aspire-realm-per-tenant)).
- **User** — a person within a tenant. Authenticated by Keycloak; profile (display name,
  locale, timezone, active device) held in IAM.
- **Role** — a named bundle of permissions (e.g. *Field Rep*, *Supervisor*, *Sales Ops*,
  *Tenant Admin*). Roles are tenant-scoped; a small set of **system role templates** seeds new
  tenants.
- **Permission** — a fine-grained capability string, `resource:action` (e.g. `outlet:write`,
  `order:submit`, `journey:read`). Modules check permissions, not roles.
- **Tenant context** — the ambient `(tenantId, userId, permissions, locale, timezone)`
  resolved from the JWT and carried through the request.

## 4. Capabilities & flows

### F1 · Login (delegated)
1. User authenticates against their tenant's Keycloak realm (OIDC authorization-code + PKCE).
2. Next.js receives tokens; the API validates the **JWT bearer** on each call.
3. The token's tenant + permission claims populate the tenant context.

### F2 · Provision a user
1. Tenant Admin creates a user (email, name, role(s), locale, timezone).
2. IAM creates the FieldKit profile and the corresponding Keycloak user in the realm; an
   invite/set-password email is sent by Keycloak.
3. Roles → permission set is materialized onto the user.

### F3 · Manage roles & permissions
- Admin creates/edits roles and toggles their permissions from the catalog of known
  permissions (contributed by each module).

### F4 · Device binding (field users)
- A field user has **one active device** ([A8](decisions-and-assumptions.md#a8--device--sync-behavior-one-active-device-auto-background-sync)).
  Registering a new device deactivates the previous one. (Device *registry* lives in Sync; IAM
  holds the "active device" pointer and authorizes the bind.)

## 5. Business rules

- **BR-IAM-1** Every tenant-owned entity carries `TenantId`; no query returns cross-tenant
  data (enforced by the global tenant filter — see [security](../architecture/16-security.md)).
- **BR-IAM-2** Authorization is **permission-based**, never role-name checks in module code.
- **BR-IAM-3** A user must have at least one role; removing the last role disables the account.
- **BR-IAM-4** Deactivating a user immediately invalidates new logins; existing tokens expire
  naturally (short access-token TTL + refresh revocation).
- **BR-IAM-5** Locale + timezone are mandatory on a user (drives i18n — [A3](decisions-and-assumptions.md#a3--internationalization-full-multi-currency--multi-language-ui)).

## 6. Requirements

| ID | Requirement | MoSCoW | Phase |
|---|---|---|---|
| IAM-01 | OIDC login via tenant Keycloak realm; JWT validated by API | Must | 1 |
| IAM-02 | Resolve tenant + permissions into an ambient tenant context per request | Must | 1 |
| IAM-03 | CRUD users (email, name, roles, locale, timezone) | Must | 1 |
| IAM-04 | CRUD roles; assign permissions from the module-contributed catalog | Must | 1 |
| IAM-05 | Permission-based authorization checks usable by all modules | Must | 1 |
| IAM-06 | Seed new tenants from system role templates | Should | 1 |
| IAM-07 | Active-device pointer + bind/rebind authorization | Should | 2 |
| IAM-08 | Self-service profile (change locale/timezone/language) | Could | 2 |
| IAM-09 | Right-to-erasure workflow for a user ([B8](decisions-and-assumptions.md#b8--privacy--gdpr-posture)) | Could | 4 |
| IAM-10 | Tenant provisioning creates the Keycloak realm automatically | Should | 2 |

## 7. Offline behavior

The field user authenticates while online; the access token + a **refresh token** are held so
the app tolerates going offline mid-session and refreshes on reconnect. Permissions are cached
on-device for the session so screens render offline. No user/role *administration* happens
offline (admin is a back-office, online activity).

## 8. Module contract (exposed to others)

- `ITenantContext` — current `(tenantId, userId, locale, timezone)`.
- `IAuthorizationService` — `Has(permission)` / policy checks.
- `IUserDirectory` — resolve display info for a user id (used by Visit/Order/Audit for actor
  attribution).
- **Permission catalog contribution** — each module registers the permission strings it owns.
- Publishes `UserDeactivated` (integration event) → Sync deactivates the user's device / blocks its
  bind ([A8](decisions-and-assumptions.md#a8--device--sync-behavior-one-active-device-auto-background-sync)).

## 9. Acceptance criteria (sample)

- A request without a valid JWT is rejected; with a valid JWT, the tenant context is populated
  and cross-tenant access is impossible even with a crafted id.
- A user with `Field Rep` role can `journey:read`/`visit:write` but not `outlet:write`.

## 10. Open questions

- Realm-per-tenant vs. single-realm-with-claim — assumption stands ([A5](decisions-and-assumptions.md#a5--authentication-keycloak-oidc-via-aspire-realm-per-tenant)).
- Do supervisors administer users, or only tenant admins? (Assumed: admins only.)
