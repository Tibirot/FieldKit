import { afterEach, vi } from "vitest";

/**
 * Every component test runs as someone who may do everything.
 *
 * Screens hide controls the signed-in user lacks the permission for, which is right — and would
 * otherwise mean every existing test asserting a button had to first prove it was allowed to see
 * one. That is noise in a test about deleting a territory.
 *
 * Mocked at the fetch boundary rather than by stubbing `usePermissions`, so the hook, its query and
 * its "pending counts as denied" rule all still run. A test about permissions overrides
 * `fetchIdentity` for itself and gets a narrower caller.
 */
vi.mock("@/lib/api/identity", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/identity")>()),
  fetchIdentity: vi.fn().mockResolvedValue({
    subject: "subject-a",
    tenant: "fieldkit-dev",
    permissions: [
      "outlet:read",
      "outlet:write",
      "channel:read",
      "channel:write",
      "territory:read",
      "territory:write",
      "orgunit:read",
      "orgunit:write",
      "position:read",
      "position:write",
      "user:read",
      "user:write",
      "role:read",
      "role:write",
      "config:read",
      "config:write",
      "product:read",
      "product:write",
    ],
  }),
}));

/**
 * Unmounts anything a component test rendered, between tests.
 *
 * Testing Library registers this for you only when Vitest's `globals` are on. This suite imports
 * `describe`/`it`/`expect` explicitly instead — a deliberate choice worth keeping — so the cleanup
 * has to be wired by hand. Without it, every render stacks into the same document and a
 * `getByRole` that should match one element starts matching three, in a way that depends on test
 * order and therefore fails somewhere other than where the mistake is.
 *
 * Guarded, because most of this suite runs in the `node` environment where there is no DOM and
 * importing Testing Library would throw at load. Component tests opt into jsdom with a
 * `@vitest-environment jsdom` docblock, and only they reach the branch below.
 */
if (typeof document !== "undefined") {
  const { cleanup } = await import("@testing-library/react");
  afterEach(cleanup);
}

/**
 * `matchMedia`, which jsdom does not implement at all.
 *
 * A gap in the environment rather than a fact about the app, so it is filled here rather than
 * guarded around in a component: code that asks "am I at desktop width" should not have to ask
 * "…and does this runtime know what width is" first, and a `typeof matchMedia === "function"` in
 * production source would be a test leaking into the thing it tests.
 *
 * **Reports no match**, which is the honest answer for a 1024×768 jsdom window with no CSS engine:
 * nothing here has resolved a media query, so claiming a breakpoint matched would be inventing one.
 * A test that cares drives the listener itself.
 */
if (typeof window !== "undefined" && typeof window.matchMedia !== "function") {
  window.matchMedia = (query: string) =>
    ({
      media: query,
      matches: false,
      onchange: null,
      addEventListener: () => {},
      removeEventListener: () => {},
      addListener: () => {},
      removeListener: () => {},
      dispatchEvent: () => false,
    }) as unknown as MediaQueryList;
}
