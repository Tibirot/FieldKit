import { ApiError } from "@/lib/api/client";
import { bindDevice, pull, push, type PushedMutation } from "@/lib/api/sync";

import { requestPersistentStorage, type FieldKitDatabase } from "./db";
import {
  markAccepted,
  markInflight,
  markRejected,
  pending,
  reclaimInflight,
} from "./outbox";
import {
  applyAssortmentChanges,
  applyConfigurationChanges,
  applyJourneyChanges,
  applyOutletAssortmentChanges,
  applyOutletChanges,
  applyProductChanges,
  ASSORTMENT,
  CONFIGURATION,
  JOURNEYS,
  OUTLET_ASSORTMENT,
  OUTLETS,
  PRODUCTS,
  pruneOutletAssortment,
  watermark,
} from "./reference";

/**
 * How many mutations go in one push.
 *
 * <b>Must stay under the server's limit of 200</b>, which refuses the whole batch above it — a
 * device that split at exactly the server's number would be one off-by-one from never draining.
 * Smaller than the cap on purpose: a batch is the unit a bad connection loses, so a hundred visits
 * re-sent is better than two hundred, and a rep whose signal comes and goes still makes progress.
 */
const PUSH_BATCH = 100;

/** What one sync run did, for the indicator and for a test to assert on. */
export type SyncResult = {
  pushed: number;
  rejected: number;
  pulled: number;
  dropped: number;
  cursor: number;
  /**
   * The run stopped early. The store is consistent either way — this says whether the device is up
   * to date or merely further along than it was.
   */
  interrupted?: "offline" | "unauthorized" | "deviceRejected" | "failed";
};

/**
 * Makes sure this browser is a registered device, and remembers which one (`OFF-12`).
 *
 * The id is stored in `meta`, so a device binds once and not once per launch. Rebinding on every
 * start would deactivate the previous registration as a swap on every start, and a rep would spend
 * their life being told to sync again from zero.
 */
export async function ensureDevice(db: FieldKitDatabase, accessToken: string): Promise<string> {
  const known = await db.meta.get("deviceId");
  if (known) return known.value;

  const device = await bindDevice(accessToken, deviceName());

  /*
   * Asked here, on first bind, and nowhere else.
   *
   * This is the moment the answer is still useful: a rep told "no" before their first offline day
   * can be asked to install the app, which is the thing that actually changes it. Asking later, with
   * a full outbox, is asking too late — and the result is not awaited into a decision because
   * nothing here should fail to bind over a storage hint (`OFF-11` surfaces it).
   */
  await requestPersistentStorage();

  await db.meta.put({ key: "deviceId", value: device.id });

  return device.id;
}

/** Something a rep would recognise in a list of their devices. Not an identifier. */
function deviceName(): string | null {
  if (typeof navigator === "undefined") return null;

  // The platform, not the full user-agent string. A rep picking their lost phone out of a list needs
  // "Android", not a version-and-engine soup that also happens to be a fingerprint.
  return navigator.userAgent.includes("Android")
    ? "Android device"
    : /iPhone|iPad/.test(navigator.userAgent)
      ? "iOS device"
      : "Browser";
}

/**
 * One sync run: push, then pull (`OFF-01`, `OFF-06`, sync engine §4).
 *
 * <b>Push first, always.</b> The day's work reaches the back office as early as possible, and a rep
 * whose battery dies during the pull has still delivered what they did. The reverse order would
 * spend the first — and possibly only — seconds of a reconnect downloading reference data nobody is
 * waiting for while the visits sit on the phone.
 *
 * <b>It never throws for an ordinary failure.</b> Losing signal halfway is the normal case, not an
 * exception: the store is left consistent, the result says how far it got, and the next run carries
 * on. What it does not do is swallow the failure — `interrupted` is the caller's signal to keep
 * showing "not synced".
 */
export async function syncOnce(
  db: FieldKitDatabase,
  accessToken: string,
  deviceId: string,
  signal?: AbortSignal,
): Promise<SyncResult> {
  const result: SyncResult = { pushed: 0, rejected: 0, pulled: 0, dropped: 0, cursor: 0 };

  const drained = await drain(db, accessToken, deviceId, result, signal);
  if (drained !== undefined) {
    result.interrupted = drained;
    result.cursor = await watermark(db, OUTLETS);

    // A pull would be refused for the same reason the push was — an expired token, a device the
    // server no longer knows — so the run stops rather than asking twice and failing twice.
    return result;
  }

  const refreshed = await refresh(db, accessToken, deviceId, result, signal);
  if (refreshed !== undefined) result.interrupted = refreshed;

  result.cursor = await watermark(db, OUTLETS);

  return result;
}

/** Sends everything queued, in batches, oldest first. */
async function drain(
  db: FieldKitDatabase,
  accessToken: string,
  deviceId: string,
  result: SyncResult,
  signal?: AbortSignal,
): Promise<SyncResult["interrupted"]> {
  for (;;) {
    const batch = await pending(db, PUSH_BATCH);
    if (batch.length === 0) return undefined;

    const mutations: PushedMutation[] = batch.map((entry) => ({
      mutationId: entry.mutationId,
      type: entry.type,
      visit: entry.payload,
    }));

    await markInflight(
      db,
      batch.map((entry) => entry.mutationId),
    );

    let response;
    try {
      response = await push(accessToken, deviceId, mutations, signal);
    } catch (error) {
      /*
       * The batch goes back to `pending` immediately rather than waiting for the startup reclaim.
       *
       * We do not know whether the server saw it — a lost response looks exactly like a lost
       * request — and re-sending is free because the ledger is keyed by ids that have not changed.
       * Leaving them `inflight` would strand them for the life of the session, which on a device
       * that stays open all day is the same as losing them.
       */
      await reclaimInflight(db);

      return classify(error);
    }

    const accepted: string[] = [];

    for (const outcome of response.results) {
      if (outcome.status === "accepted") {
        accepted.push(outcome.mutationId);
        continue;
      }

      await markRejected(db, outcome.mutationId, outcome.reason ?? undefined, outcome.detail ?? undefined);
      result.rejected += 1;
    }

    await markAccepted(db, accepted);
    result.pushed += accepted.length;

    // A server that answered about fewer mutations than were sent would otherwise leave the rest
    // `inflight` and spin this loop forever on a batch that never shrinks.
    if (response.results.length < batch.length) {
      await reclaimInflight(db);

      return "failed";
    }
  }
}

/** Takes one page of reference changes and applies it with its watermark. */
async function refresh(
  db: FieldKitDatabase,
  accessToken: string,
  deviceId: string,
  result: SyncResult,
  signal?: AbortSignal,
): Promise<SyncResult["interrupted"]> {
  // One cursor per entity type, read together and sent together. They advance independently, so a
  // device far behind on outlets and current on journeys asks for exactly that.
  const cursors = {
    outlets: await watermark(db, OUTLETS),
    journeys: await watermark(db, JOURNEYS),
    configuration: await watermark(db, CONFIGURATION),
    products: await watermark(db, PRODUCTS),
    assortment: await watermark(db, ASSORTMENT),
    outletAssortment: await watermark(db, OUTLET_ASSORTMENT),
  };

  let response;
  try {
    response = await pull(accessToken, deviceId, cursors, signal);
  } catch (error) {
    return classify(error);
  }

  const { outlets, journeys, configuration, products, assortment, outletAssortment } =
    response.changes;

  // Two transactions, not one. Failing to store the round must not undo outlets that already
  // landed — a device that got half a pull keeps the half it got, and asks for the rest next time.
  await applyOutletChanges(db, outlets, response.snapshotVersion);
  await applyJourneyChanges(db, journeys);
  await applyConfigurationChanges(db, configuration);
  await applyProductChanges(db, products);
  await applyAssortmentChanges(db, assortment);
  await applyOutletAssortmentChanges(db, outletAssortment);

  // After the outlets have landed, because it reads what the device now holds. An outlet that left
  // the rep's territory takes its overrides with it, and the server sends no tombstone for them —
  // the device works it out from the outlet tombstone it was already sent.
  await pruneOutletAssortment(db);

  await db.meta.put({ key: "lastSyncAt", value: String(Date.now()) });

  /*
   * Summed over every page rather than named one at a time.
   *
   * The hand-written version silently stopped counting configuration when slice 8b added it — the
   * totals only feed an indicator, so nothing failed, and the test that would have caught it did not
   * assert on them. A list is harder to forget to extend than an expression.
   */
  const pages = [outlets, journeys, configuration, products, assortment, outletAssortment];

  result.pulled += pages.reduce((total, page) => total + page.upserts.length, 0);
  result.dropped += pages.reduce((total, page) => total + page.tombstones.length, 0);

  return undefined;
}

/**
 * Turns a failure into something the UI can act on.
 *
 * The distinction that matters is *whose problem it is*: a dropped connection is the rep's normal
 * day and should say nothing louder than "not synced"; an expired session needs them to sign in
 * again; a device the server has revoked needs them to bind again, and no amount of retrying will
 * help. Collapsing these into "sync failed" is how a rep ends up retrying for an hour against a
 * 401.
 */
function classify(error: unknown): NonNullable<SyncResult["interrupted"]> {
  if (error instanceof ApiError) {
    if (error.status === 401) return "unauthorized";

    // 404 (this device is not registered to you) and 409 (it was replaced) both mean the same thing
    // to a client: the binding is stale and pulling again will not fix it.
    if (error.status === 404 || error.status === 409 || error.status === 403) return "deviceRejected";

    return "failed";
  }

  // `fetch` rejects with a TypeError when the network is unreachable. Nothing else here distinguishes
  // "the tunnel ate it" from "the phone is in a lift", and neither needs to.
  return "offline";
}

/** A live sync manager: one run at a time, and a run whenever the device comes back. */
export type SyncManager = {
  /** Runs now if nothing is running, otherwise returns the run already in progress. */
  syncNow: () => Promise<SyncResult>;
  /** Removes the listeners. Called when the signed-in user changes, or on unmount. */
  stop: () => void;
};

/**
 * Starts syncing for one signed-in rep (`OFF-06`).
 *
 * <b>Single-flight.</b> Tapping "Sync now" while a reconnect-triggered run is in progress joins that
 * run rather than starting a second one. Two concurrent runs would push the same batch twice —
 * harmless server-side thanks to the ledger, but it doubles the traffic on the one connection the
 * rep is short of, and the second run's pull could apply an older page over a newer one.
 *
 * <b>Stranded work is reclaimed once, at startup.</b> Rows left `inflight` by a device that was
 * killed mid-push are waiting on a connection that no longer exists; nothing will ever answer them.
 * Done here rather than per run, because a run's *own* in-flight batch must not be reclaimed
 * underneath it.
 */
export function startSync(
  db: FieldKitDatabase,
  accessToken: () => string | null,
  deviceId: string,
): SyncManager {
  let running: Promise<SyncResult> | null = null;

  /*
   * Started eagerly and awaited by the first run, with the rejection handled here.
   *
   * A floating promise that can reject is an unhandled rejection in every browser and a noisy one
   * in error reporting — and this one can be created and never awaited, because a session may end
   * without a single sync. Swallowing it is right on its merits too: if the reclaim fails, the
   * stranded rows stay exactly where they already were, and refusing to sync over it would turn a
   * cosmetic problem into a total one.
   */
  const started = reclaimInflight(db).catch(() => 0);

  const syncNow = (): Promise<SyncResult> => {
    if (running) return running;

    running = (async () => {
      await started;

      const token = accessToken();

      // No token is not a failure to report — it is a rep who is signed out, and the outbox waits.
      if (!token) {
        return { pushed: 0, rejected: 0, pulled: 0, dropped: 0, cursor: 0, interrupted: "unauthorized" as const };
      }

      return syncOnce(db, token, deviceId);
    })().finally(() => {
      running = null;
    });

    return running;
  };

  /*
   * `online` fires when the OS thinks there is a network, which is optimistic — it says nothing
   * about whether the server is reachable through it. That is fine: a run that fails costs one
   * request and returns `offline`, and the next event tries again.
   *
   * The `catch` matters. A *transport* failure comes back as an `interrupted` result, but a storage
   * failure — quota, eviction mid-write — throws, and an unhandled rejection from an event listener
   * is the kind of error that shows up in a bug report as "the app just stops syncing". The caller
   * that asked for the run gets the rejection; a run nobody asked for has nowhere to report it.
   */
  const onOnline = () => {
    syncNow().catch(() => {});
  };

  if (typeof window !== "undefined") window.addEventListener("online", onOnline);

  return {
    syncNow,
    stop: () => {
      if (typeof window !== "undefined") window.removeEventListener("online", onOnline);
    },
  };
}
