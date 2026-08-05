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
  - The worker is **built by a post-`next build` step** (`frontend/scripts/build-sw.mjs`), not by a
    bundler plugin — see [Building the service worker](#building-the-service-worker-phase-0).
  - **One manifest per locale** (`/{locale}/manifest.webmanifest`), because a manifest carries
    user-visible text and a launch URL, neither of which can be locale-neutral under the
    always-prefixed routing of [ADR-0010](0010-internationalization.md). Identity (`id`) and
    `scope` stay shared, so a second install re-points the first rather than adding a duplicate
    home-screen icon, and the locale switcher doesn't eject the installed app to a browser tab.
- **i18n:** `next-intl` for multi-language UI + locale-aware formatting; timezone-correct display
  ([A3](../../product/decisions-and-assumptions.md#a3--internationalization-full-multi-currency--multi-language-ui)).
- **Deployment:** runs as a container (standalone output) on ACA
  ([ADR-0011](0011-deployment-azure-container-apps.md)), composed by Aspire
  ([ADR-0003](0003-adopt-dotnet-aspire.md)).

## State: three kinds, and only one library

Recorded after the fact, because it was being decided by default. TanStack Query was named here from
the start and IndexedDB with it; **client state was never chosen**, so the shell was built with React
state and one context simply because that is what you reach for first. Writing it down is what makes
the next person's Zustand import a decision rather than a reflex.

| Kind | Lives in | Example |
|---|---|---|
| **Server state** | TanStack Query | The outlet base, the field-definition catalogue |
| **Durable device state** | IndexedDB via Dexie, read reactively with `liveQuery` | A rep's journey, a draft order, the outbox |
| **Ephemeral UI state** | React `useState`, lifted to the nearest common owner | A filter, an open dialog, a half-edited import grid |

**There is no global client store, and adding one needs a reason that passes this test:** state that
must be shared *across routes* **and** is neither server-backed nor device-backed. Anything
server-backed belongs in the query cache, where invalidation is already solved; anything a rep must
not lose when the tab dies belongs in IndexedDB, which is the whole point of
[ADR-0007](0007-offline-sync-strategy.md).

Applying that test to what is actually coming: the import grid lives in one route; a draft order is
device-backed; filters are one screen's business. **So the honest expectation is that this product
never needs one** — which is worth saying, because the failure mode is not a missing library. It is a
second copy of an outlet in a store, disagreeing with the query cache about which one is current, and
no rule about which wins.

**Form state is React Hook Form's**, with [Zod](https://zod.dev) schemas. It is the conventional
pairing, which matters more than it sounds: a contributor opening this repo recognises it without
reading a rationale, and an unconventional hand-rolled equivalent has to be justified to every
reader. Two rules keep it honest — **schemas for config-driven fields are generated from the
descriptor**, never typed out, so they cannot drift from the server that owns the rules
([CFG-02](../../product/14-configuration.md)); and **every message comes from the message catalogue**,
because Zod's defaults are developer text in one language.

Two things follow from having no store. **Cross-route state gets a URL** — a filter or a selected row
belongs in search params, where it is shareable, restorable and back-button-correct for free. And
**auth stays a context**, because it is genuinely global, genuinely small, and has exactly one writer.

## Options considered

| Option | Verdict | Why |
|---|---|---|
| Keep Vite SPA | Rejected | Simpler offline story, but misses the Next.js skill target and RSC/back-office benefits. |
| Next.js, SSR-first everywhere | Rejected | Fights the offline requirement — the field app can't depend on a reachable server per request. |
| **Next.js App Router, offline-first (client store in the field, RSC in back office)** | **Chosen** | Gets modern Next.js where it helps and a robust client/offline model where it's required. |
| Separate SPA (field) + Next.js (back office) | Rejected | Two codebases/design systems; more to build and keep consistent. |

## Building the service worker *(Phase 0)*

The obvious way to get a Workbox service worker into a Next app is [`@serwist/next`](https://github.com/serwist/serwist)
(the maintained successor to `next-pwa`). Both are **webpack plugins**, and Next 16 builds with
**Turbopack** by default — so adopting either means pinning the project to `next build --webpack`
indefinitely, on the bundler Next is steadily moving away from. Serwist's Turbopack support has been
open and unstarted since January 2024.

That is a large, permanent cost for a file that has nothing to do with bundling the app, so FieldKit
drives Workbox directly instead:

1. `workbox-build`'s `getManifest()` computes the precache manifest from the hashed `.next/static`
   output, plus one **offline shell page per locale** versioned by the Next build id.
2. **esbuild** bundles `frontend/sw/index.js` — with its `workbox-*` imports — into a classic
   worker at `public/sw.js`, substituting the manifest in via `define`.

`injectManifest()` is the usual one-call alternative and does *not* work here: it substitutes the
manifest but leaves the bare `import` statements in place, which a classic service worker cannot
evaluate. The cost of this approach is one build script; the benefit is that the choice of app
bundler and the choice of service-worker tooling stop being coupled at all.

**Scope of the worker:** the app *shell* only — HTML, JS, CSS, fonts. It deliberately never caches
API responses. Offline **data** is IndexedDB plus the [sync engine](../12-offline-sync-engine.md)
([ADR-0007](0007-offline-sync-strategy.md)); an HTTP cache sitting alongside it would be a second
source of truth with none of the sync engine's conflict rules.

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
- **The service worker only exists in a production build.** `public/sw.js` is a build artifact, so
  `next dev` has no PWA at all; verifying it means `npm run build && npm start` (the `frontend-prod`
  launch configuration). This is deliberate — a service worker over a dev server caches stale
  modules and produces bewildering HMR failures — but it does mean PWA regressions can only be
  caught after a build, not during development.
- **`output: "standalone"` does not copy `public/`.** The container image must copy it explicitly
  or the manifest, icons and service worker 404 in a deployed environment while working perfectly
  in `npm start`. To be verified against the Aspire-generated Dockerfile when
  [ADR-0011](0011-deployment-azure-container-apps.md) deployment lands.

**Follow-up:** Phase 0 migration ([roadmap](../../roadmap.md)); document the front-end module
structure and the client-store contract with the [sync engine](../12-offline-sync-engine.md).
