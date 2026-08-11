/**
 * What the browser will let this device keep (`OFF-11`, offline-behavior §2) — W9 slice 11.
 *
 * <b>Two different questions, and a rep only cares about one of them.</b> *How full is the quota*
 * decides whether the next pull can be written; *is the origin persistent* decides whether any of it
 * survives a browser that decides to reclaim space. The second is the one that loses a day's work,
 * and it is the one nothing surfaced until now — `requestPersistentStorage` has been asking since
 * W5 and throwing the answer away.
 */

/** How much room there is, and whether the browser has promised to keep it. */
export type StorageStatus = {
  /** Bytes this origin is using, or `null` when the browser will not say. */
  usedBytes: number | null;
  /** Bytes it is allowed, or `null`. */
  quotaBytes: number | null;
  /** `0`–`1`, or `null` when either figure is missing. */
  fraction: number | null;
  /**
   * Whether the browser has agreed not to evict this origin.
   *
   * `null` means it does not implement the API at all, which is not the same as "no" — treating an
   * old browser as *at risk* would put a warning in front of a rep who can do nothing about it.
   */
  persisted: boolean | null;
};

/**
 * The share of the quota above which a rep should be told.
 *
 * <b>Eighty per cent, and the number is a judgement rather than a measurement.</b> A device that is
 * genuinely full fails at the write, which the sync manager already surfaces; this exists to give a
 * rep warning *before* the day they cannot sync, and a threshold much higher would not.
 */
export const PRESSURE_FRACTION = 0.8;

/**
 * Reads the browser's own estimate.
 *
 * <b>Both halves are optional and are handled separately.</b> `estimate()` and `persisted()` are
 * independent APIs with independent support, and a browser answering one and not the other is
 * ordinary — collapsing them into a single "unknown" would hide the half that did answer.
 *
 * The estimate is deliberately approximate: browsers pad and round it to avoid being a
 * fingerprinting surface. Nothing here does arithmetic that would care.
 */
export async function storageStatus(): Promise<StorageStatus> {
  const storage = globalThis.navigator?.storage;

  const persisted = storage?.persisted ? await safely(() => storage.persisted()) : null;

  if (!storage?.estimate) {
    return { usedBytes: null, quotaBytes: null, fraction: null, persisted };
  }

  const estimate = await safely(() => storage.estimate());
  const usedBytes = estimate?.usage ?? null;
  const quotaBytes = estimate?.quota ?? null;

  return {
    usedBytes,
    quotaBytes,
    // Guarded against a zero quota as well as a missing one: some browsers report `0` in private
    // mode, and dividing by it would produce `Infinity` and a warning about nothing.
    fraction: usedBytes !== null && quotaBytes ? usedBytes / quotaBytes : null,
    persisted,
  };
}

/**
 * Runs a browser API that is allowed to say no.
 *
 * `estimate()` and `persisted()` both reject in some private-browsing modes rather than answering,
 * and a screen that shows how much room is left is not worth an unhandled rejection. `null` reads
 * the same as "the browser did not say", which is a state the caller already handles.
 */
async function safely<T>(read: () => Promise<T>): Promise<T | null> {
  try {
    return await read();
  } catch {
    return null;
  }
}

/**
 * Whether the rep should be told something, and what.
 *
 * <b>Unsent work is what turns a fact into a warning.</b> A device at ninety per cent of quota with
 * an empty outbox is a device that will drop some cached images; the same device holding a day of
 * visits is one that can lose them. Warning on the storage figure alone would train a rep to
 * dismiss the one that matters.
 */
export type StorageConcern =
  /** Nothing worth saying. */
  | "none"
  /** The quota is nearly used up. */
  | "full"
  /** The browser has not promised to keep this origin, and there is work it could take. */
  | "evictable";

export function concernOf(status: StorageStatus, pending: number): StorageConcern {
  if (status.fraction !== null && status.fraction >= PRESSURE_FRACTION) return "full";

  // `persisted === null` is a browser that does not implement the API, and is deliberately not a
  // warning: a rep cannot act on it, and neither can we.
  if (status.persisted === false && pending > 0) return "evictable";

  return "none";
}
