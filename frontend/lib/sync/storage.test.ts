import { afterEach, describe, expect, it } from "vitest";

import { concernOf, PRESSURE_FRACTION, storageStatus, type StorageStatus } from "@/lib/sync/storage";

/**
 * What the browser will let this device keep (`OFF-11`) — W9 slice 11.
 *
 * The interesting cases are all the ones where a browser declines to answer, because that is the
 * ordinary case rather than the edge: private modes reject, older engines have neither API, and a
 * screen that treated silence as bad news would warn a rep who can do nothing about it.
 */
function withStorage(implementation: Partial<StorageManager> | undefined) {
  const original = Object.getOwnPropertyDescriptor(globalThis.navigator, "storage");

  Object.defineProperty(globalThis.navigator, "storage", {
    value: implementation,
    configurable: true,
  });

  return () => {
    if (original) Object.defineProperty(globalThis.navigator, "storage", original);
    else Reflect.deleteProperty(globalThis.navigator as object, "storage");
  };
}

let restore: (() => void) | undefined;

afterEach(() => {
  restore?.();
  restore = undefined;
});

function status(overrides: Partial<StorageStatus> = {}): StorageStatus {
  return { usedBytes: null, quotaBytes: null, fraction: null, persisted: null, ...overrides };
}

describe("reading what the browser will say", () => {
  it("reports usage, quota and the share of it in use", async () => {
    restore = withStorage({
      estimate: async () => ({ usage: 250, quota: 1000 }),
      persisted: async () => true,
    });

    expect(await storageStatus()).toEqual({
      usedBytes: 250,
      quotaBytes: 1000,
      fraction: 0.25,
      persisted: true,
    });
  });

  it("keeps the half that answered when the other does not exist", async () => {
    // Independent APIs with independent support. Collapsing both into "unknown" would throw away
    // the persistence answer, which is the one that decides whether work survives.
    restore = withStorage({ persisted: async () => false });

    expect(await storageStatus()).toEqual(status({ persisted: false }));
  });

  it("survives a browser that rejects rather than answering", async () => {
    // Private modes do this. An unhandled rejection here would take out the device screen over a
    // number that is nice to have.
    restore = withStorage({
      estimate: () => Promise.reject(new Error("nope")),
      persisted: () => Promise.reject(new Error("nope")),
    });

    expect(await storageStatus()).toEqual(status());
  });

  it("answers with nothing at all when the API is absent", async () => {
    restore = withStorage(undefined);

    expect(await storageStatus()).toEqual(status());
  });

  it("does not divide by a zero quota", async () => {
    // Reported by some browsers in private mode. `Infinity` here would read as permanent pressure
    // and warn on every visit.
    restore = withStorage({ estimate: async () => ({ usage: 0, quota: 0 }) });

    expect((await storageStatus()).fraction).toBeNull();
  });
});

describe("what is worth telling the rep", () => {
  it("says nothing about a device with room", () => {
    expect(concernOf(status({ fraction: 0.1, persisted: true }), 3)).toBe("none");
  });

  it("warns when the quota is nearly gone", () => {
    expect(concernOf(status({ fraction: PRESSURE_FRACTION }), 0)).toBe("full");
  });

  it("warns about eviction only when there is work to lose", () => {
    // The judgement the whole function exists for. A non-persistent device with an empty outbox
    // stands to lose cached images; the same device holding a day of visits stands to lose the day.
    expect(concernOf(status({ persisted: false }), 0)).toBe("none");
    expect(concernOf(status({ persisted: false }), 1)).toBe("evictable");
  });

  it("stays quiet when the browser never said whether it would keep the data", () => {
    // `null` is "no such API", not "no". A rep cannot act on it and neither can we, so warning
    // would be noise they learn to scroll past — and then miss the one that mattered.
    expect(concernOf(status({ persisted: null }), 5)).toBe("none");
  });

  it("prefers the full warning when both apply, because it blocks the fix", () => {
    // Syncing is what clears an evictable device's risk, and a full one may not be able to write
    // the pull that follows. Telling a rep to sync first would be advice they cannot take.
    expect(concernOf(status({ fraction: 0.95, persisted: false }), 2)).toBe("full");
  });
});
