"use client";

import { useEffect } from "react";

import { requestPersistentStorage } from "@/lib/sync/db";

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

        /*
         * The shared one from `lib/sync/db`, not a private copy (W9 slice 11).
         *
         * There were two implementations of this — one here returning nothing and untested, one
         * there returning whether the browser agreed and covered by three tests. They answered the
         * same question and only one of them said what the answer was, which is precisely the
         * information `OFF-11` needed to surface and could not reach.
         */
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

