import { waitFor } from "@testing-library/react";

/**
 * Asserts something the screen arrives at, rather than something it is at this instant.
 *
 * <b>Written because the same mistake has now been made four times</b> (W11½ R2). The shape is
 * always this: wait for a *store* condition, then assert on the *DOM*.
 *
 * ```ts
 * await waitFor(async () => expect(await db.outbox.count()).toBe(1));
 * expect(screen.queryByText("…")).toBeNull();   // ← still on screen for another microtask
 * ```
 *
 * The store write and the re-render are two moments. Dexie's `liveQuery` re-emits after the
 * transaction commits, React renders after that, and an assertion in between sees the old screen.
 * It passes on a fast laptop and fails on CI — which is exactly what happened during W11 slice 14,
 * on a pull request that touched none of the code involved.
 *
 * <b>Why a helper rather than a rule to remember.</b> Three of the four sites already had a comment
 * about flakiness above them; one of them "fixed" it by lengthening the wrong wait. A convention
 * that has been re-learned four times is a convention that wants a function.
 *
 * The whole body is retried, so put the query inside it — `eventually(() => expect(x).toBe(y))`
 * with `x` read outside would retry the same stale value until it times out.
 */
export function eventually(assertion: () => void): Promise<void> {
  return waitFor(assertion);
}
