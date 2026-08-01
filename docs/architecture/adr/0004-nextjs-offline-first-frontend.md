# ADR-0004: Next.js offline-first front end

- **Status:** Accepted
- **Date:** 2026-08
- **Deciders:** Tiberiu Socea
- **Related:** [ADR-0007](0007-offline-sync-strategy.md), [sync engine](../12-offline-sync-engine.md),
  decisions [A3](../../product/decisions-and-assumptions.md#a3--internationalization-full-multi-currency--multi-language-ui) ·
  [A7](../../product/decisions-and-assumptions.md#a7--ui-toolkit-shadcnui--tailwind)

## Context

FieldKit needs **one** web front end that serves two very different experiences: a **mobile-first,
offline-first field PWA** and a **desktop-first back office**. Next.js is also a deliberate skill
target (it's on the CV; this proves depth). The scaffold currently ships a **Vite + React** SPA —
migrating it is Phase 0 work, and the choice has real consequences for the offline model, so it
gets an ADR.

The tension: Next.js's headline features (Server Components, server actions, SSR) assume a server
is reachable — but the field app's defining requirement is that **it works with no server at
all** ([ADR-0007](0007-offline-sync-strategy.md)).

## Decision

Adopt **Next.js (App Router) + React 19 + TypeScript** as the single front-end app, architected
**offline-first**:

- **One app, two segments:** a `(field)` route group (mobile-first, offline PWA) and a
  `(back-office)` route group (desktop-first, mostly online). Shared design system
  ([A7: shadcn/ui + Tailwind](../../product/decisions-and-assumptions.md#a7--ui-toolkit-shadcnui--tailwind)).
- **Client-driven data in the field.** The field app is effectively a **client app inside
  Next.js**: it reads from the on-device store (IndexedDB) via the [sync engine](../12-offline-sync-engine.md),
  **not** from Server Components at request time. RSC/SSR are used for the **back office** and for
  the app shell, not for offline data paths.
- **PWA:** installable, with a **service worker** (Workbox) caching the app shell for offline
  boot, and **IndexedDB (Dexie)** as the durable local store. **TanStack Query** manages
  server-state where online.
- **i18n:** `next-intl` for multi-language UI + locale-aware formatting; timezone-correct display
  ([A3](../../product/decisions-and-assumptions.md#a3--internationalization-full-multi-currency--multi-language-ui)).
- **Deployment:** runs as a container (standalone output) on ACA
  ([ADR-0011](0011-deployment-azure-container-apps.md)), composed by Aspire
  ([ADR-0003](0003-adopt-dotnet-aspire.md)).

## Options considered

| Option | Verdict | Why |
|---|---|---|
| Keep Vite SPA | Rejected | Simpler offline story, but misses the Next.js skill target and RSC/back-office benefits. |
| Next.js, SSR-first everywhere | Rejected | Fights the offline requirement — the field app can't depend on a reachable server per request. |
| **Next.js App Router, offline-first (client store in the field, RSC in back office)** | **Chosen** | Gets modern Next.js where it helps and a robust client/offline model where it's required. |
| Separate SPA (field) + Next.js (back office) | Rejected | Two codebases/design systems; more to build and keep consistent. |

## Consequences

**Positive**
- One codebase, one design system, two tailored experiences.
- Field app is genuinely offline-first; back office gets RSC/server actions where useful.
- Demonstrates Next.js App Router **and** a non-trivial PWA/offline architecture.

**Negative / costs**
- **Discipline required:** the field data path must **not** silently depend on the server (no
  RSC-fetched data on offline screens). This is a review/architecture-test concern, not a
  framework guarantee.
- Migrating the scaffold Vite→Next.js is real Phase-0 work (re-wire the Aspire JS hosting, PWA
  setup, standalone output).
- Service-worker + IndexedDB add front-end complexity — inherent to the offline requirement.

**Follow-up:** Phase 0 migration ([roadmap](../../roadmap.md)); document the front-end module
structure and the client-store contract with the [sync engine](../12-offline-sync-engine.md).
