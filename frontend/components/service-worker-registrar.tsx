"use client";

import { useEffect } from "react";

/**
 * Registers the app-shell service worker and asks for durable storage (OFF-10, OFF-02).
 *
 * Renders nothing — it exists so the registration runs once per document, from the locale layout,
 * rather than being duplicated in every route group.
 *
 * `public/sw.js` is a *build artefact* (`scripts/build-sw.mjs` writes it after `next build`), so it
 * does not exist under `next dev`. Registering there would log a 404 on every page load and, worse,
 * teach you to ignore service-worker errors. To exercise the PWA locally, run a production build
 * and `npm start`.
 */
export function ServiceWorkerRegistrar() {
  useEffect(() => {
    if (process.env.NODE_ENV !== "production" || !("serviceWorker" in navigator)) {
      return;
    }

    const register = async () => {
      try {
        await navigator.serviceWorker.register("/sw.js", { scope: "/" });
        await requestPersistentStorage();
      } catch (error) {
        // A failed registration degrades the app to online-only; it must never break the page.
        console.error("Service worker registration failed", error);
      }
    };

    void register();
  }, []);

  return null;
}

/**
 * Captured work has to survive eviction, and browsers — iOS especially — will clear storage under
 * pressure unless the origin is marked persistent (offline-behavior §2). This is best-effort by
 * design: the browser may grant it silently, prompt, or refuse, and refusing is not an error.
 *
 * Asking only when not already persisted keeps repeat visits from re-prompting.
 */
async function requestPersistentStorage(): Promise<void> {
  if (!navigator.storage?.persist || !navigator.storage.persisted) {
    return;
  }

  if (!(await navigator.storage.persisted())) {
    await navigator.storage.persist();
  }
}
