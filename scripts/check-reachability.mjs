#!/usr/bin/env node
/**
 * Nothing is built that nobody can reach — W12, the 14 Aug regression's own recommendation.
 *
 * Two sweeps found nine defects and **six were one shape**: a capability that exists everywhere
 * except at the point where somebody would use it. A screen with no link to it (`F1`), a mutation
 * type with no producer (`F7`, `F2`), a component that is never mounted (`F4`). Every unit passed
 * its own tests in all six, because none of them is a bug *in* a unit — they are absences of an
 * **edge** between two things that each work, and a test suite is organised by unit.
 *
 * So this checks the edges, in the two places the regression named and a third the W12½ navigation
 * audit added:
 *
 *  1. **Every mutation type the protocol carries has a producer on the device.** The server's push
 *     endpoint accepts a closed set; the sync manager routes a closed set; `lib/` enqueues a closed
 *     set. All three must be the same set. `RescheduledCall` was in the first two and not the third
 *     for five weeks (`F2`), and `UnplannedCall` before it (`F7`).
 *
 *  2. **Every field-app route is linked from somewhere.** Order and audit capture were reachable
 *     only from a workflow step, so a channel with no workflow could be visited and nothing could be
 *     done in it — `ORD-01` and `AUD-01`, both Musts, behind optional configuration (`F1`).
 *
 *  3. **Every back-office route has a navigation item, and every navigation item a route.** Not the
 *     same failure as (2): none of the 28 back-office routes was ever *unlinked*, and both regression
 *     sweeps confirmed it. What was missing was a **level** — of the seventeen screens that deserve a
 *     navigation item, six had one, and eleven were reachable only by landing on a section index and
 *     spotting the right button in a row of outline links. Checked in both directions, because
 *     `W11½ R1` checked one and understated the drift for weeks in the other.
 *
 * <b>A source scan rather than a runtime crawl</b>, for the reason `check-vector-readers.mjs` gives:
 * a runtime check would need the app to report which edges it exercised, which is more apparatus
 * than the property deserves and would itself need checking. The cost is that this sees *text* — a
 * link assembled from pieces no literal contains would read as missing. That is the failure mode to
 * expect, and it fails loudly rather than silently.
 *
 * Check 3 is the exception and reads the navigation **model** rather than text, because there is a
 * model to read: `NAVIGATION` is plain data in a module with no React in it, so Node's type
 * stripping imports it as-is. A regex over `href:` would have worked and would have been worse — it
 * could not name the section a missing screen belongs to, and it would go quiet on a reformat.
 *
 * <b>What it cannot see</b> is worth stating as plainly as what it can. `F3` — a value computed on
 * an aggregate and absent from its own DTO — is an edge one layer below anything here: no route, no
 * mutation, just a field a mapper did not carry. This gate would have passed the day `F3` was
 * introduced, and a reader who takes it as *coverage of the shape* rather than *coverage of two
 * instances of the shape* will be wrong in the same direction the regression was.
 *
 * Run by the `frontend` CI job, and standalone with:
 *
 *     node scripts/check-reachability.mjs
 */

import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { fileURLToPath } from "node:url";

import { NAVIGATION } from "../frontend/components/back-office/navigation.ts";

const root = fileURLToPath(new URL("..", import.meta.url));

const FIELD_ROUTES = join(root, "frontend", "app", "[locale]", "(field)");
const BACK_OFFICE_ROUTES = join(root, "frontend", "app", "[locale]", "(back-office)");
const PUSH_ENDPOINTS = join(root, "FieldKit.Modules.Sync", "PushEndpoints.cs");
const SYNC_MANAGER = join(root, "frontend", "lib", "sync", "manager.ts");
const PRODUCERS = join(root, "frontend", "lib");

/** Where a link may live: the field app's own pages and the components they render. */
const LINK_SOURCES = [join(root, "frontend", "app"), join(root, "frontend", "components")];

const failures = [];

function walk(directory, extensions) {
  const found = [];

  for (const entry of readdirSync(directory)) {
    if (entry === "node_modules" || entry === "bin" || entry === "obj" || entry === ".next") {
      continue;
    }

    const path = join(directory, entry);

    if (statSync(path).isDirectory()) {
      found.push(...walk(path, extensions));
    } else if (extensions.some((extension) => entry.endsWith(extension))) {
      found.push(path);
    }
  }

  return found;
}

/**
 * Fails the run rather than reporting nothing found.
 *
 * <b>W11½ R1's lesson, and the reason it is a helper rather than a habit.</b> That gate's first
 * draft asked "is every built contract listed?" against a set that came back empty, and passed —
 * vacuously, silently, and for two directions of drift at once. A scan whose *input* is empty has
 * not checked anything, and the only difference between that and a green build is a message nobody
 * reads.
 */
function required(values, what, where) {
  if (values.length === 0) {
    console.error(`Found no ${what} in ${where}.`);
    console.error("That is a move, a rename or a broken pattern — not a codebase with none.");
    process.exit(1);
  }

  return values;
}

function matches(text, pattern) {
  return [...text.matchAll(pattern)].map((match) => match[1]);
}

// ── 1. Every mutation type has a producer ────────────────────────────────────────────────────────

/*
 * Three lists, read from the three places that each own one:
 *
 *   - what the server will apply    `PushEndpoints.ApplyAsync`'s switch arms
 *   - what the device routes        `slotOf` in the sync manager
 *   - what the device produces      `enqueue({ type: … })` under `lib/`
 *
 * A type in the first two and not the third is work the protocol carries and no rep can create —
 * `F2` and `F7` exactly. A type in the third and not the first is work a rep can create and the
 * server answers `sync.push.typeUnsupported` to, which strands it in the outbox forever.
 */
const push = readFileSync(PUSH_ENDPOINTS, "utf8");
const manager = readFileSync(SYNC_MANAGER, "utf8");

const accepted = required(
  matches(push, /nameof\((\w+)\)\s*=>/g),
  "mutation types the server accepts",
  "PushEndpoints.cs",
);

/*
 * The manager names all but one type explicitly and falls through to `visit` for the rest, so the
 * fallback's type has to be read from the server's list rather than from `slotOf`. Taking it as
 * "whatever the server accepts and the manager does not name" keeps the two in step by
 * construction: a seventh type added to the server and not to `slotOf` shows up as unrouted here.
 */
const routed = required(
  matches(manager, /type === "(\w+)"/g),
  "mutation types the sync manager routes",
  "manager.ts",
);

/*
 * Read from files that touch the outbox at all, rather than from every `type:` in `lib/`.
 *
 * Both narrowings are load-bearing and neither is obvious. **Not `enqueue(` alone**, because
 * `local-visit.ts` writes `db.outbox.add` inline on purpose — its row has to share the check-out
 * transaction — so an `enqueue`-anchored scan would report `CapturedVisit` as producible by nobody.
 * And **not every `type:` in the tree**, because `manifest.ts` and `oidc.ts` have their own; those
 * happen to be lower-case today, so the capital in the pattern is doing that work by luck, and this
 * makes it a decision instead.
 */
const produced = required(
  walk(PRODUCERS, [".ts", ".tsx"])
    .filter((path) => !path.includes(".test."))
    .map((path) => readFileSync(path, "utf8"))
    .filter((text) => text.includes("outbox"))
    .flatMap((text) => matches(text, /type:\s*"([A-Z]\w+)"/g)),
  "mutation types the device produces",
  "frontend/lib",
);

const unproducible = accepted.filter((type) => !produced.includes(type));
const unrouted = accepted.filter((type) => type !== "CapturedVisit" && !routed.includes(type));
const unsupported = produced.filter((type) => !accepted.includes(type));

if (unproducible.length > 0) {
  failures.push({
    title: "Mutation types the protocol carries and no device can produce",
    items: unproducible,
    why: "A route for work a rep cannot create. This is regression F2 and F7, which sat for weeks.",
    fix: "Add a writer under frontend/lib, or drop the type from PushEndpoints if the work is gone.",
  });
}

if (unrouted.length > 0) {
  failures.push({
    title: "Mutation types the server accepts and the sync manager does not route",
    items: unrouted,
    why: "The manager would send these in the `visit` slot, and the server would read a null and refuse.",
    fix: "Add the type to slotOf in frontend/lib/sync/manager.ts.",
  });
}

if (unsupported.length > 0) {
  failures.push({
    title: "Mutation types the device produces and the server will not apply",
    items: unsupported,
    why: "The push answers sync.push.typeUnsupported, and the work never leaves the outbox.",
    fix: "Add an arm to PushEndpoints.ApplyAsync, or stop enqueueing the type.",
  });
}

// ── 2. Every field route is linked from somewhere ────────────────────────────────────────────────

/**
 * A page's route, with its dynamic segments flattened.
 *
 * `[visitId]` and `${visit.id}` are the same hole seen from the two ends, so both become `*` and the
 * comparison is between shapes rather than between a folder name and an expression.
 */
function routeOf(page) {
  const path = relative(FIELD_ROUTES, page).split(sep).slice(0, -1);

  return `/${path.map((segment) => (segment.startsWith("[") ? "*" : segment)).join("/")}`;
}

const routes = required(
  walk(FIELD_ROUTES, ["page.tsx"]).map(routeOf),
  "field-app routes",
  "app/[locale]/(field)",
);

/*
 * Every `/field…` path that appears in a string or template literal, with interpolations flattened
 * the same way. Trailing query strings are dropped — `?call=` decides what a screen *does*, never
 * which screen it is.
 */
const links = new Set(
  LINK_SOURCES.flatMap((directory) => walk(directory, [".tsx", ".ts"]))
    .filter((path) => !path.includes(".test."))
    .flatMap((path) => matches(readFileSync(path, "utf8"), /["'`](\/field[^"'`]*)["'`]/g))
    .map((link) => link.replace(/\$\{[^}]*\}/g, "*").split("?")[0].replace(/\/$/, "")),
);

/**
 * The app's own front door, which nothing inside it links *to*.
 *
 * Exempt because it is the target of the sign-in redirect and the shell's home link, not because it
 * is unreachable — and named here rather than skipped quietly, so that the day a second route earns
 * an exemption somebody has to write down why.
 */
const ENTRY = "/field";

const unlinked = routes.filter((route) => route !== ENTRY && !links.has(route));

if (unlinked.length > 0) {
  failures.push({
    title: "Field-app routes nothing links to",
    items: unlinked,
    why: "A screen reachable only by typing its URL. This is regression F1, which hid order capture.",
    fix: "Link it from the screen a rep would arrive from, or delete the route.",
  });
}

// ── 3. Every back-office route has a navigation item, and every item a route ─────────────────────

/** A page's route, with its dynamic segments left alone — here they are the thing being classified. */
function backOfficeRouteOf(page) {
  const path = relative(BACK_OFFICE_ROUTES, page).split(sep).slice(0, -1);

  return path.length === 0 ? "/" : `/${path.join("/")}`;
}

function covers(route, pathname) {
  return pathname === route || pathname.startsWith(`${route}/`);
}

const backOfficeRoutes = required(
  walk(BACK_OFFICE_ROUTES, ["page.tsx"]).map(backOfficeRouteOf),
  "back-office routes",
  "app/[locale]/(back-office)",
);

const screens = required(
  NAVIGATION.flatMap((group) => group.items).flatMap((item) => item.screens ?? []),
  "navigation screens",
  "navigation.ts",
);

/**
 * Create forms, which are reached from the list they add a row to and are not places.
 *
 * <b>Listed rather than matched on a `/new` suffix</b>, for the reason `ENTRY` is named above: the
 * day a third route earns the exemption, somebody has to write it down. A suffix rule would let a
 * screen slip past by being called `new`, and the whole point of this check is that a screen cannot
 * slip past.
 */
const CREATE_FORMS = ["/outlets/new", "/configuration/surveys/new"];

/*
 * Two rules rather than one, because the single-rule version passes vacuously.
 *
 * "Every route is under some screen" is satisfied by `/products` alone — it is a prefix of all six
 * screens in its own section — so a model containing nothing but the six section indexes would pass
 * while describing exactly the state this check exists to end. What holds instead:
 *
 *   • a **static** route is a screen of its own, unless it is a create form;
 *   • a **dynamic** route is a record detail, and belongs to the screen above it.
 */
const unnavigable = backOfficeRoutes.filter((route) => {
  if (route.includes("[")) return !screens.some((screen) => covers(screen.href, route));

  return !CREATE_FORMS.includes(route) && !screens.some((screen) => screen.href === route);
});

/*
 * And the other direction, which is the half `W11½ R1` left out of the module registry and paid for.
 * A screen whose page has been deleted or moved is a navigation item that renders a live link to a
 * 404 — quieter than a missing item, because the nav looks complete.
 */
const dangling = screens.filter((screen) => !backOfficeRoutes.includes(screen.href));

if (unnavigable.length > 0) {
  failures.push({
    title: "Back-office routes with no navigation item",
    items: unnavigable,
    why: "A screen reachable only by knowing it is there. This is what the W12½ audit found eleven of.",
    fix: "Add it to the owning section's `screens` in navigation.ts, with the permissions it needs.",
  });
}

if (dangling.length > 0) {
  failures.push({
    title: "Navigation items whose route does not exist",
    items: dangling.map((screen) => `${screen.key} → ${screen.href}`),
    why: "A live link to a 404, and harder to notice than a missing item because the nav looks whole.",
    fix: "Restore the page, or drop the screen from navigation.ts.",
  });
}

// ── Report ───────────────────────────────────────────────────────────────────────────────────────

if (failures.length > 0) {
  console.error("Something is built that nobody can reach:\n");

  for (const failure of failures) {
    console.error(`  ${failure.title}:`);
    for (const item of failure.items) console.error(`    - ${item}`);
    console.error(`    ${failure.why}`);
    console.error(`    ${failure.fix}\n`);
  }

  process.exit(1);
}

console.log(
  `${accepted.length} mutation type(s), each routed and produced; ` +
    `${routes.length} field route(s), each linked; ` +
    `${backOfficeRoutes.length} back-office route(s) across ${screens.length} navigation screen(s).`,
);
