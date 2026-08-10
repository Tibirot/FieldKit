"use client";

import { liveQuery } from "dexie";
import { useEffect, useState } from "react";

/**
 * Re-runs an IndexedDB query whenever anything it read changes (`OFF-05`).
 *
 * <b>Twenty lines instead of `dexie-react-hooks`</b>, and the reason is that `liveQuery` — the part
 * that does the actual work — ships inside `dexie` itself. The package would add a dependency, a
 * lockfile regeneration and a second opinion about React versions to wrap an observable in a
 * `useEffect`, which is this.
 *
 * <b>Why a live query rather than refreshing after each sync.</b> The pending count changes for two
 * unrelated reasons: the sync manager drains the outbox, and a screen *enqueues* into it when a rep
 * checks out of a visit. A provider that refreshed only after syncing would show a stale zero for
 * the whole time between capturing work and reconnecting — which is precisely the window the
 * indicator exists for.
 *
 * `initial` is returned until the first result arrives, so a caller never renders `undefined` and
 * the indicator never flashes an empty state on mount.
 */
export function useLive<T>(query: () => Promise<T>, initial: T, deps: unknown[]): T {
  const [value, setValue] = useState<T>(initial);

  useEffect(() => {
    const subscription = liveQuery(query).subscribe({
      next: setValue,

      // A failed observation is not worth tearing a screen down for: the indicator degrades to its
      // last known value, which is wrong by exactly one sync rather than blank. The store failing is
      // surfaced by the operations that actually write, not by the one that counts.
      error: () => {},
    });

    return () => subscription.unsubscribe();

    // The query closure is rebuilt every render, so it cannot be the dependency — the caller names
    // what it actually depends on, the way `useEffect` has always asked them to.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  return value;
}
