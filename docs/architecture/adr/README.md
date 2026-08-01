# Architecture Decision Records

An **ADR** captures a single architectural decision, the context that forced it, the options
considered, and the consequences accepted. They are immutable: when a decision changes, we
add a new ADR that **supersedes** the old one rather than editing the record. This gives the
project an honest paper trail of *why* it looks the way it does — which for a portfolio piece
is half the point.

Format: [Michael Nygard's template](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions).

## Index

| ADR | Title | Status |
|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted |
| [0002](0002-modular-monolith.md) | Adopt a modular monolith | Accepted |
| [0003](0003-adopt-dotnet-aspire.md) | Adopt .NET Aspire for orchestration | Accepted |
| [0004](0004-nextjs-offline-first-frontend.md) | Next.js offline-first front end | Accepted |
| [0005](0005-postgres-schema-per-module.md) | PostgreSQL with schema-per-module | Accepted |
| [0006](0006-in-process-messaging-and-outbox.md) | In-process messaging & transactional outbox | Accepted |
| [0007](0007-offline-sync-strategy.md) | Offline sync strategy | Accepted |
| [0008](0008-authentication-and-multitenancy.md) | Authentication & multi-tenancy (Keycloak, realm-per-tenant) | Accepted |
| [0009](0009-config-driven-customization.md) | Config-driven customization model | Accepted |
| [0010](0010-internationalization.md) | Internationalization (currency & language) | Accepted |
| [0011](0011-deployment-azure-container-apps.md) | Deployment target: Azure Container Apps | Accepted |

**Statuses:** Proposed · Accepted · Planned (intended, not yet fully specified) ·
Superseded by ADR-XXXX · Deprecated.
