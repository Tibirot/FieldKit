/**
 * FieldKit app-shell service worker (OFF-10, ADR-0004).
 *
 * This file is the *source*. `scripts/build-sw.mjs` bundles it and substitutes the Workbox
 * precache manifest, writing `public/sw.js`. It is never served as-is.
 *
 * Scope note: this worker caches the **app shell** — the HTML, JS, CSS and fonts needed to boot.
 * It deliberately does not cache business data. Offline *data* is IndexedDB plus the sync engine
 * (ADR-0007), which lands in Phase 2; caching API responses here would create a second, competing
 * source of truth with none of the sync engine's conflict rules.
 */

import { CacheableResponsePlugin } from "workbox-cacheable-response";
import { clientsClaim } from "workbox-core";
import { ExpirationPlugin } from "workbox-expiration";
import { cleanupOutdatedCaches, matchPrecache, precacheAndRoute } from "workbox-precaching";
import { NavigationRoute, registerRoute } from "workbox-routing";
import { CacheFirst, NetworkFirst, NetworkOnly, StaleWhileRevalidate } from "workbox-strategies";

/** Both are substituted at build time by `scripts/build-sw.mjs` (esbuild `define`). */
const PRECACHE_MANIFEST = self.__WB_MANIFEST;
const DEFAULT_LOCALE = self.__FIELDKIT_DEFAULT_LOCALE__;

const PAGE_CACHE = "fieldkit-pages";
const ASSET_CACHE = "fieldkit-assets";
const IMAGE_CACHE = "fieldkit-images";

/**
 * The offline fallback pages, keyed by locale, discovered from the precache manifest rather than
 * from a hard-coded list — the build script decides which locales ship, and this reads back its
 * decision. Adding a language stays a content task (ADR-0010); nothing here changes.
 */
const offlineByLocale = new Map(
  PRECACHE_MANIFEST.map((entry) => (typeof entry === "string" ? entry : entry.url))
    .filter((url) => /^\/[^/]+\/offline$/.test(url))
    .map((url) => [url.split("/")[1], url]),
);
const defaultOffline = offlineByLocale.get(DEFAULT_LOCALE);

precacheAndRoute(PRECACHE_MANIFEST);

// Drops precaches written by earlier Workbox versions of this worker.
cleanupOutdatedCaches();

/**
 * Serve the offline shell for the locale the rep was heading to, so a Romanian user offline does
 * not land on an English page. Unprefixed paths (`/`) cannot be resolved — offline there is no
 * server to run the locale negotiator in `proxy.ts` — so they get the default locale, which the
 * build substitutes in rather than this inferring it from manifest ordering.
 */
async function offlineFallback(url) {
  if (!defaultOffline) {
    return Response.error();
  }

  const locale = new URL(url).pathname.split("/")[1];
  const fallback = await matchPrecache(offlineByLocale.get(locale) ?? defaultOffline);

  return fallback ?? Response.error();
}

// ── Routes, in match order ──────────────────────────────────────────────────

/**
 * The API is never cached, ever. Reads are tenant-scoped and authorization-sensitive, and writes
 * are the sync engine's idempotent push (ADR-0007) — a replayed cached response would either leak
 * another tenant's data or silently swallow a mutation. Registered first so nothing below can
 * claim these requests by accident.
 */
registerRoute(({ url }) => url.pathname.startsWith("/api/"), new NetworkOnly());

/**
 * Navigations: try the network briefly, fall back to the last good copy of that page, then to the
 * offline shell. The timeout matters more than it looks — a rep in a back room is usually on a
 * *technically connected* but dead link, where a plain `fetch` hangs rather than failing.
 */
registerRoute(
  new NavigationRoute(
    new NetworkFirst({
      cacheName: PAGE_CACHE,
      networkTimeoutSeconds: 3,
      plugins: [
        new CacheableResponsePlugin({ statuses: [200] }),
        new ExpirationPlugin({ maxEntries: 50 }),
        { handlerDidError: async ({ request }) => offlineFallback(request.url) },
      ],
    }),
  ),
);

/**
 * Build output is content-hashed and immutable, so cache-first is safe and a revalidation request
 * would be pure waste. Precaching already covers everything present at build time; this catches
 * chunks that a running client requests afterwards.
 */
registerRoute(
  ({ url }) => url.pathname.startsWith("/_next/static/"),
  new CacheFirst({
    cacheName: ASSET_CACHE,
    plugins: [new CacheableResponsePlugin({ statuses: [200] })],
  }),
);

/** Icons and optimized images — worth having offline, not worth going stale forever. */
registerRoute(
  ({ request, url }) => request.destination === "image" || url.pathname.startsWith("/_next/image"),
  new StaleWhileRevalidate({
    cacheName: IMAGE_CACHE,
    plugins: [
      new CacheableResponsePlugin({ statuses: [200] }),
      new ExpirationPlugin({ maxEntries: 60, maxAgeSeconds: 30 * 24 * 60 * 60 }),
    ],
  }),
);

// ── Lifecycle ───────────────────────────────────────────────────────────────

// Take over pages that loaded before this worker activated, so the first visit is protected too.
clientsClaim();

/**
 * Activation is *not* automatic. A waiting worker that skips ahead deletes the previous build's
 * chunks out from under a page that is still running it, which breaks lazily-loaded routes
 * mid-visit — the worst possible moment for a rep with an unsynced outbox. The page asks for the
 * swap instead; the "update ready" prompt that sends this message arrives with the field shell.
 */
self.addEventListener("message", (event) => {
  if (event.data?.type === "SKIP_WAITING") {
    self.skipWaiting();
  }
});
