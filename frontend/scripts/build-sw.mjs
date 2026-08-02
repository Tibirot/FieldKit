import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";

import { build } from "esbuild";
import { getManifest } from "workbox-build";

import { routing } from "../i18n/routing.ts";

/**
 * Builds `public/sw.js` from `sw/index.js` (OFF-10).
 *
 * This runs as a *post-build step* rather than through `@serwist/next` or `next-pwa`, both of
 * which are webpack plugins. Next 16 builds with Turbopack by default, and adopting either would
 * mean pinning the whole project to `next build --webpack` indefinitely to get a service worker —
 * paying a bundler downgrade for a file that has nothing to do with bundling the app. Driving
 * Workbox directly costs this one script and stays bundler-agnostic (ADR-0004).
 *
 * Two steps, because `workbox-build` does not bundle. `getManifest` computes *what* to precache;
 * esbuild turns the `workbox-*` imports in `sw/index.js` into a single classic worker script and
 * substitutes the manifest in as it goes. (`injectManifest` is the usual one-call alternative, but
 * it only does the substitution — it leaves bare `import` statements in place, which a classic
 * service worker cannot evaluate.)
 *
 * Ordering matters: `next build` must have written `.next/` before this runs. See the `build`
 * script in package.json.
 */

const frontendDir = fileURLToPath(new URL("..", import.meta.url));

/**
 * The app-shell pages to precache, on top of the globbed build output.
 *
 * These are HTML routes, so they are not in `.next/static` and have to be named explicitly.
 * They are versioned by the Next build id: a new deploy changes the revision, Workbox refetches,
 * and the offline shell can never be left pointing at chunks that no longer exist.
 *
 * **The default locale must come first** — `sw/index.js` uses the first entry as the fallback for
 * paths with no locale prefix, which offline it cannot negotiate.
 */
export function appShellEntries(locales, defaultLocale, buildId) {
  const ordered = [defaultLocale, ...locales.filter((locale) => locale !== defaultLocale)];

  return ordered.map((locale) => ({ url: `/${locale}/offline`, revision: buildId }));
}

async function main() {
  let buildId;
  try {
    buildId = (await readFile(new URL(".next/BUILD_ID", `file://${frontendDir}`), "utf8")).trim();
  } catch {
    console.error("No .next/BUILD_ID — run `next build` before building the service worker.");
    process.exitCode = 1;
    return;
  }

  const { manifestEntries, count, size, warnings } = await getManifest({
    globDirectory: ".next",
    // Everything needed to boot and render the shell. Not images: those are runtime-cached, so a
    // large asset can never push the precache over a device's quota.
    globPatterns: ["static/**/*.{js,css,woff,woff2}"],
    modifyURLPrefix: { "static/": "/_next/static/" },
    // Next content-hashes its build output, so the URL already is the version.
    dontCacheBustURLsMatching: /static\//,
    additionalManifestEntries: appShellEntries(routing.locales, routing.defaultLocale, buildId),
    maximumFileSizeToCacheInBytes: 5 * 1024 * 1024,
  });

  for (const warning of warnings) {
    console.warn(`workbox: ${warning}`);
  }

  await build({
    entryPoints: ["sw/index.js"],
    outfile: "public/sw.js",
    bundle: true,
    // A classic worker, not a module worker: `type: "module"` service workers are still not
    // supported in Firefox, and the registration in components/service-worker-registrar.tsx
    // deliberately doesn't ask for one.
    format: "iife",
    target: "es2022",
    minify: true,
    define: {
      "self.__WB_MANIFEST": JSON.stringify(manifestEntries),
      // Workbox guards its debug logging on this. A service worker has no `process`, so leaving it
      // unsubstituted would throw on evaluation rather than merely log too much.
      "process.env.NODE_ENV": JSON.stringify("production"),
    },
  });

  console.log(`Service worker written to public/sw.js — ${count} files precached (${size} bytes).`);
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  await main();
}
