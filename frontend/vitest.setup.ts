import { afterEach } from "vitest";

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
