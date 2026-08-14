#!/usr/bin/env node
/**
 * The offline shell is actually in the build (`OFF-10`) — W12.
 *
 * <b>CI has run `npm run build` since the beginning, and never looked at what it produced.</b> The
 * production build is the only place `public/sw.js` exists at all — `next dev` does not register a
 * service worker — so the app's central claim, that a rep can work with no signal, was the one
 * thing no automated check exercised. Both regression sweeps recorded that; this is the cheap half
 * of the answer.
 *
 * <b>What it can catch that the unit tests cannot.</b> `build-sw.test.ts` tests `appShellEntries`,
 * a pure function over the locale list — so it passes whether or not the page those URLs point at
 * still exists. Delete `app/[locale]/offline/page.tsx` and: the entries are still emitted, the
 * manifest still names them, every test in the repository still passes, and the worker fails to
 * install on every device in the field. Service-worker installation failures are invisible — no
 * console the rep can see, no request that fails loudly — so this is a defect that ships silently
 * and is discovered by a rep in a basement.
 *
 * <b>What it deliberately does not do</b> is run the thing. Registering the worker, going offline
 * and asserting the shell renders is a Playwright job and belongs with the `Week 14` E2E work the
 * delivery plan already schedules. This checks the *artefact*: that the file exists, that its
 * placeholders were substituted, that the manifest is real, and that everything it promises to
 * precache is something the build can serve.
 *
 * Run by the `frontend` CI job after `npm run build`, and standalone with:
 *
 *     npm run check:sw
 */

import { existsSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

import { routing } from "../i18n/routing.ts";

const frontend = fileURLToPath(new URL("..", import.meta.url));

const WORKER = join(frontend, "public", "sw.js");
const BUILD = join(frontend, ".next");

const failures = [];

function fail(title, detail) {
  failures.push({ title, detail });
}

// ── The worker exists, and is a build rather than a stub ──────────────────────────────────────────

if (!existsSync(WORKER)) {
  console.error("public/sw.js does not exist — `npm run build` did not finish, or it stopped");
  console.error("after `next build` without running scripts/build-sw.mjs.");
  process.exit(1);
}

const worker = readFileSync(WORKER, "utf8");

/*
 * A floor rather than an exact size.
 *
 * The bundle is Workbox plus the routing in `sw/index.js`, minified — tens of kilobytes. The number
 * here is far below that and exists to catch one specific thing: an esbuild run that wrote an empty
 * or near-empty file and exited zero, which would leave every check below passing vacuously on a
 * string with nothing in it. This is the same guard the reachability gate learned to add after
 * W11½ R1's registry passed against an empty set.
 */
if (statSync(WORKER).size < 4096) {
  fail(
    "public/sw.js is too small to be a real bundle",
    "esbuild wrote a file but not a worker. Check scripts/build-sw.mjs for a silent failure.",
  );
}

// ── Every `define` was substituted ────────────────────────────────────────────────────────────────

/*
 * An unsubstituted placeholder is a worker that throws on evaluation, which the browser reports as
 * a failed registration and the app never mentions. `build-sw.mjs` names this hazard for
 * `process.env.NODE_ENV` specifically — "leaving it unsubstituted would throw on evaluation rather
 * than merely log too much" — and the same is true of the other two.
 */
for (const placeholder of ["__WB_MANIFEST", "__FIELDKIT_DEFAULT_LOCALE__", "process.env.NODE_ENV"]) {
  if (worker.includes(placeholder)) {
    fail(
      `\`${placeholder}\` survived into public/sw.js`,
      "esbuild did not substitute it, so the worker throws when the browser evaluates it — which "
        + "presents as a registration that silently never happens.",
    );
  }
}

// ── The precache manifest is real, and names an offline page per locale ───────────────────────────

/*
 * Read back out of the minified bundle rather than recomputed.
 *
 * Recomputing it here would be a second implementation of `appShellEntries`, agreeing with the
 * first by construction and proving nothing about the file a browser will actually fetch. The
 * entries survive minification as an object literal, so they can be read.
 */
const entries = [...worker.matchAll(/\{url:"([^"]+)",revision:(null|"[^"]*")\}/g)].map((match) => ({
  url: match[1],
  revision: match[2] === "null" ? null : match[2].slice(1, -1),
}));

if (entries.length === 0) {
  fail(
    "public/sw.js carries no precache manifest",
    "Either `getManifest` matched nothing, or the entry shape changed and this scan needs updating. "
      + "Both are worth stopping for: a worker with an empty manifest caches nothing and fails "
      + "quietly.",
  );
}

for (const locale of routing.locales) {
  const url = `/${locale}/offline`;
  const entry = entries.find((candidate) => candidate.url === url);

  if (!entry) {
    fail(
      `The precache manifest does not name \`${url}\``,
      "A rep whose device is set to this locale has no shell to fall back to.",
    );

    continue;
  }

  // Versioned by the build id. Null here would mean the shell is cached forever against chunks a
  // later deploy has already removed — the failure `appShellEntries` sets a revision to prevent.
  if (!entry.revision) {
    fail(
      `\`${url}\` is precached with no revision`,
      "A deploy will not invalidate it, so the shell can outlive the chunks it references.",
    );
  }
}

// ── …and the page those URLs point at is in the build ─────────────────────────────────────────────

/*
 * <b>The check the unit tests structurally cannot make.</b>
 *
 * `appShellEntries` derives its URLs from the *locale list*, not from the route tree — so the
 * manifest promises `/en/offline` whether or not anything serves it. The route is dynamic rather
 * than prerendered, so there is no HTML file to look for; what proves it is part of the build is
 * its compiled server entry.
 */
const offlineRoute = join(BUILD, "server", "app", "[locale]", "offline", "page.js");

if (!existsSync(offlineRoute)) {
  fail(
    "The offline page is not in the build output",
    "`app/[locale]/offline/page.tsx` did not compile to "
      + ".next/server/app/[locale]/offline/page.js, so every URL the manifest promises 404s at "
      + "install time — and a failed service-worker installation is silent.",
  );
}

// ── Report ────────────────────────────────────────────────────────────────────────────────────────

if (failures.length > 0) {
  console.error("The offline shell would not work in production:\n");

  for (const failure of failures) {
    console.error(`  ${failure.title}`);
    console.error(`    ${failure.detail}\n`);
  }

  process.exit(1);
}

console.log(
  `public/sw.js: ${entries.length} precache entr(ies), `
    + `an offline shell for ${routing.locales.join(", ")}, every placeholder substituted.`,
);
