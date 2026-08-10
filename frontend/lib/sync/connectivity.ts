"use client";

import { useSyncExternalStore } from "react";

function subscribe(notify: () => void): () => void {
  window.addEventListener("online", notify);
  window.addEventListener("offline", notify);

  return () => {
    window.removeEventListener("online", notify);
    window.removeEventListener("offline", notify);
  };
}

/**
 * Whether the device thinks it has a network (`OFF-05`).
 *
 * <b>The OS's opinion, and it is optimistic.</b> `navigator.onLine` is true on a captive portal, on
 * a wifi network with no route out, and on a train pulling into a tunnel until the stack notices. It
 * says "an interface is up", not "the server is reachable".
 *
 * That is why the indicator built on this shows *pending work* rather than a green tick: what a rep
 * needs to know is whether their visits have reached the back office, and only a completed sync can
 * answer that. Connectivity is the cheap signal that explains *why* they have not — see
 * `SyncIndicator`, which treats offline as an explanation and the pending count as the fact.
 *
 * <b>`useSyncExternalStore` rather than `useState` in an effect</b>, which is what this was first.
 * `navigator.onLine` is an external mutable value with a subscription — the exact shape this hook
 * exists for — and doing it by hand means a render that says "online" before the effect corrects it.
 * The server snapshot is `true` for the same reason: a server-rendered shell must not announce that
 * a rep is offline and then take it back a frame later.
 */
export function useConnectivity(): boolean {
  return useSyncExternalStore(
    subscribe,
    () => navigator.onLine,
    () => true,
  );
}
