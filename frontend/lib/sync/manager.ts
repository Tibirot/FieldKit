import { ApiError } from "@/lib/api/client";
import { bindDevice, pull, push, type PushedMutation } from "@/lib/api/sync";
import { applyOrderVerdicts, ORDERS } from "@/lib/orders/verdicts";
import { uploadPhotos } from "@/lib/photos/upload";

import { requestPersistentStorage, type FieldKitDatabase, type OutboxEntry } from "./db";
import { flushDeviceEvents } from "./device-log";
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
  applyOrderMinimumChanges,
  applyOutletAssortmentChanges,
  applyOutletChanges,
  applyPriceAssignmentChanges,
  applyPriceLineChanges,
  applyPriceListChanges,
  applyProductChanges,
  applyPromotionAssignmentChanges,
  applyScoreWeightChanges,
  applyTaxRateChanges,
  applySurveyChanges,
  applyPromotionChanges,
  ASSORTMENT,
  CONFIGURATION,
  JOURNEYS,
  ORDER_MINIMUMS,
  OUTLET_ASSORTMENT,
  OUTLETS,
  PRICE_ASSIGNMENTS,
  PRICE_LINES,
  PRICE_LISTS,
  PRODUCTS,
  PROMOTION_ASSIGNMENTS,
  SCORE_WEIGHTS,
  TAX_RATES,
  SURVEYS,
  PROMOTIONS,
  pruneOutletAssortment,
  pruneOutletPriceAssignments,
  pruneOutletPromotionAssignments,
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
  /** Photographs that reached object storage this run (`OFF-08`) — W11 slice 12b. */
  uploaded: number;
  /**
   * Photographs still on the device after it.
   *
   * Failures *and* the ones given up on, because the rep needs one number for "pictures the back
   * office does not have" — the difference between them is the uploader's business, not theirs.
   */
  awaitingUpload: number;
  /**
   * The run stopped early. The store is consistent either way — this says whether the device is up
   * to date or merely further along than it was.
   */
  interrupted?: "offline" | "unauthorized" | "deviceRejected" | "failed";
};

/**
 * The bind in flight for one database, so two callers cannot each start one.
 *
 * <b>Binding is a write, and the check that guards it is a *read that has already happened*.</b>
 * Two callers arriving together both miss the stored id and both post — and the second one is
 * refused by the unique index that makes "one active device per rep" true, which the server reports
 * as a 500. Found in the browser: React's development double-invocation of effects is exactly two
 * callers arriving together, and it turned the field shell's first launch into a bind, a swap, and
 * an error screen.
 *
 * Keyed by database name rather than a single slot, because the name is per tenant *and* subject —
 * two reps signed in on one browser are two devices, and collapsing them would give the second one
 * the first one's id.
 */
const binding = new Map<string, Promise<string>>();

/**
 * Makes sure this browser is a registered device, and remembers which one (`OFF-12`).
 *
 * The id is stored in `meta`, so a device binds once and not once per launch. Rebinding on every
 * start would deactivate the previous registration as a swap on every start, and a rep would spend
 * their life being told to sync again from zero.
 *
 * Once *per launch* is not the same as once *per caller*, which is what {@link binding} is for.
 */
export async function ensureDevice(db: FieldKitDatabase, accessToken: string): Promise<string> {
  const known = await db.meta.get("deviceId");
  if (known) return known.value;

  // Checked and set with no `await` between them, so there is no point for a second caller to
  // interleave: whoever gets here first owns the request and everyone else waits on it.
  const started = binding.get(db.name);
  if (started) return started;

  const bind = mint(db, accessToken).finally(() => binding.delete(db.name));
  binding.set(db.name, bind);

  return bind;
}

async function mint(db: FieldKitDatabase, accessToken: string): Promise<string> {
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
  const result: SyncResult = {
    pushed: 0,
    rejected: 0,
    pulled: 0,
    dropped: 0,
    cursor: 0,
    uploaded: 0,
    awaitingUpload: 0,
  };

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

  /*
   * Photographs last, and on their own terms (`OFF-08`, `B5`, sync engine §6) — W11 slice 12b.
   *
   * <b>After the pull, not before.</b> A JPEG is twenty times a visit's JSON, and the reference data
   * a rep needs to work the *next* shop is worth more than the picture of the last one. A rep whose
   * signal dies here has delivered their day's work and refreshed their round; only evidence lags.
   *
   * <b>It runs even when the pull was interrupted.</b> The two transports fail for different reasons
   * — a pull refused for a stale cursor says nothing about whether a blob can be PUT — and skipping
   * the upload because the pull stumbled would make photographs hostage to a queue they are not in.
   * What it does not do is overwrite `interrupted`: a run that uploaded everything and failed to pull
   * is still a run that did not finish.
   */
  const photos = await uploadPhotos(db, accessToken, new Date(), signal);

  result.uploaded = photos.uploaded;

  /*
   * One number for "evidence the back office does not have" (`OFF-05`) — W11 slice 13b.
   *
   * Failures, the ones given up on, **and** the ones in storage the server has not acknowledged.
   * They are three states to the uploader and one fact to a rep: a picture their supervisor cannot
   * see. An unconfirmed photograph is the mildest of the three and still belongs here, because the
   * alternative is telling a rep everything is in while the server is still expecting something.
   */
  result.awaitingUpload = photos.failed + photos.abandoned + photos.awaitingConfirmation;

  /*
   * Last, and unconditionally (`observability §5`) — W13 slice 8.
   *
   * <b>Last</b> because a rep's work outranks a report about a rep's device: if signal lasts for one
   * request, it should carry a visit. <b>Unconditional</b> — including after an interrupted run —
   * because a device that keeps failing to sync is precisely the device whose diagnostics nobody
   * ever sees, and gating this on a clean run would silence it exactly when it matters.
   *
   * It cannot fail this function: `flushDeviceEvents` swallows its own errors and answers 0, which
   * is the same posture the recorder takes. A run must not be reported as interrupted because a
   * diagnostic report did not go through.
   */
  await flushDeviceEvents(db, accessToken, deviceId, signal);

  return result;
}

/**
 * The property a mutation's payload travels under, decided by its type (W9 slice 9).
 *
 * <b>The wire format is a typed property per kind, not a `payload` blob</b>, so `type` alone is not
 * enough — the server binds `notVisited` into a `NotVisitedCall` and `visit` into a `CapturedVisit`,
 * and a payload under the wrong name is a **400**, not a refusal. That is worse than it sounds: a
 * refusal is recorded and stops, while a 400 fails the whole batch and the device retries it every
 * time it reconnects, forever.
 *
 * Which is exactly what happened. Until this slice the outbox had one mutation type, so the manager
 * hard-coded `visit: entry.payload` and was right by accident. The unit tests mock the API, so
 * nothing caught it — the live round trip did.
 *
 * `visit` is the fallback rather than a throw: an unknown type is the server's to refuse by name
 * (`sync.push.typeUnsupported`), and a client that dropped the mutation instead would leave work in
 * the outbox with nothing ever explaining why.
 */
function slotOf(
  type: string,
): "visit" | "notVisited" | "rescheduled" | "unplanned" | "audit" | "order" {
  if (type === "NotVisitedCall") return "notVisited";
  if (type === "RescheduledCall") return "rescheduled";
  if (type === "UnplannedCall") return "unplanned";
  if (type === "CapturedAudit") return "audit";
  if (type === "CapturedOrder") return "order";

  return "visit";
}

/**
 * Drops mutations whose turn has not come (`OFF-04`) — W11 slice 8c.
 *
 * <b>An order may not be pushed before the visit it was taken during.</b> `OrderIngestService`
 * refuses an order for a visit it has never seen (`order.ingest.visitUnknown`), and that refusal is
 * right — an order has to belong to a call this rep made. What was wrong was the *order of the
 * sending*: `CapturedVisit` is only enqueued at check-out, so an order submitted at the counter is
 * genuinely the older row and went first, was refused, was marked `failed`, and was never retried.
 * The rep lost the order and the indicator said "Everything synced".
 *
 * <b>Found in a browser, and neither suite could have found it.</b> The device tests mock the sync
 * API so nothing meets a real refusal, and every server test pushes a visit before an order because
 * one that wanted the order to succeed had to. The seam is the ordering *between* two mutations.
 *
 * <b>Held, not reordered.</b> Sending the visit first inside the same batch would work only if the
 * server applied a batch in array order — which it does, but relying on that would put this rule in
 * two places and make it a property of the wire rather than of the device. Holding the order for a
 * later run keeps the rule here, in one function, where it is testable.
 *
 * <b>The starvation case is real and bounded.</b> A batch of a hundred held orders would push
 * nothing — but that needs a rep to have submitted a hundred orders without checking out once, and
 * a visit ends with the check-out that releases them. One order per visit is the shape the aggregate
 * enforces.
 *
 * <b>An audit is held on the same terms, and it was waiting to fail the same way</b> (W11 slice 9a).
 * `IAuditIngest` refuses an audit for a visit it has never seen (`UnknownVisit`), an audit is sealed
 * at the shelf while `CapturedVisit` is still only enqueued at check-out, and `markRejected` writes
 * `failed` — every step of 8c's bug, on a mutation type that did not exist when 8c was written. It is
 * gated here rather than found in a browser a second time.
 *
 * The list of dependent types is named once, below, so the next mutation that belongs to a visit is
 * a line rather than a rediscovery.
 */
const AFTER_THE_VISIT = new Set(["CapturedOrder", "CapturedAudit"]);

async function sendable(db: FieldKitDatabase, batch: OutboxEntry[]): Promise<OutboxEntry[]> {
  const held = new Set<string>();

  for (const entry of batch) {
    if (!AFTER_THE_VISIT.has(entry.type)) continue;

    const visitId = (entry.payload as { visitId?: string } | null)?.visitId;
    if (!visitId) continue;

    const visit = await db.visits.get(visitId);

    /*
     * A visit this device does not hold is *sent*, not held.
     *
     * The device cannot reason about it — and holding forever would be a worse failure than the one
     * being fixed, because the mutation would sit in the outbox with nothing that could ever release
     * it. The server decides, which is what it did before this function existed.
     */
    if (!visit) continue;

    // Still open: no `CapturedVisit` has been enqueued yet, so the server cannot possibly know it.
    if (visit.status !== "checkedOut") {
      held.add(entry.mutationId);
      continue;
    }

    // Checked out, but its own mutation has not landed — pending, in flight, or refused.
    const outstanding = await db.outbox.where("subjectId").equals(visitId).count();

    if (outstanding > 0) held.add(entry.mutationId);
  }

  return held.size === 0 ? batch : batch.filter((entry) => !held.has(entry.mutationId));
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
    const queued = await pending(db, PUSH_BATCH);
    if (queued.length === 0) return undefined;

    const batch = await sendable(db, queued);

    /*
     * Everything in this batch is waiting on something else. Returning rather than looping is what
     * stops the drain spinning: `pending` would hand back the same rows forever, and the thing they
     * are waiting for is a *later* mutation that this run will never reach.
     */
    if (batch.length === 0) return undefined;

    const mutations: PushedMutation[] = batch.map((entry) => ({
      mutationId: entry.mutationId,
      type: entry.type,
      [slotOf(entry.type)]: entry.payload,
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
    priceLists: await watermark(db, PRICE_LISTS),
    priceLines: await watermark(db, PRICE_LINES),
    priceAssignments: await watermark(db, PRICE_ASSIGNMENTS),
    promotions: await watermark(db, PROMOTIONS),
    promotionAssignments: await watermark(db, PROMOTION_ASSIGNMENTS),
    surveys: await watermark(db, SURVEYS),
    scoreWeights: await watermark(db, SCORE_WEIGHTS),
    taxRates: await watermark(db, TAX_RATES),
    orderMinimums: await watermark(db, ORDER_MINIMUMS),
    orders: await watermark(db, ORDERS),
  };

  let response;
  try {
    response = await pull(accessToken, deviceId, cursors, signal);
  } catch (error) {
    return classify(error);
  }

  const {
    outlets,
    journeys,
    configuration,
    products,
    assortment,
    outletAssortment,
    priceLists,
    priceLines,
    priceAssignments,
    promotions,
    promotionAssignments,
    surveys,
    scoreWeights,
    taxRates,
    orderMinimums,
    orders,
  } = response.changes;

  // Two transactions, not one. Failing to store the round must not undo outlets that already
  // landed — a device that got half a pull keeps the half it got, and asks for the rest next time.
  await applyOutletChanges(db, outlets, response.snapshotVersion);
  await applyJourneyChanges(db, journeys);
  await applyConfigurationChanges(db, configuration);
  await applyProductChanges(db, products);
  await applyAssortmentChanges(db, assortment);
  await applyOutletAssortmentChanges(db, outletAssortment);
  await applyPriceListChanges(db, priceLists);
  await applyPriceLineChanges(db, priceLines);
  await applyPriceAssignmentChanges(db, priceAssignments);
  await applyPromotionChanges(db, promotions);
  await applyPromotionAssignmentChanges(db, promotionAssignments);
  await applySurveyChanges(db, surveys);
  await applyScoreWeightChanges(db, scoreWeights);
  await applyTaxRateChanges(db, taxRates);
  await applyOrderMinimumChanges(db, orderMinimums);

  /*
   * The one that is not reference data: what the back office made of orders this device sent
   * (`BR-ORD-9`, regression F5) — W12 F5b. Merged into rows the device already holds rather than
   * replacing them, which is why it does not live with the others in `reference.ts`.
   */
  await applyOrderVerdicts(db, orders);

  // After the outlets have landed, because it reads what the device now holds. An outlet that left
  // the rep's territory takes its overrides with it, and the server sends no tombstone for them —
  // the device works it out from the outlet tombstone it was already sent.
  await pruneOutletAssortment(db);
  await pruneOutletPriceAssignments(db);
  await pruneOutletPromotionAssignments(db);

  await db.meta.put({ key: "lastSyncAt", value: String(Date.now()) });

  /*
   * Summed over every page rather than named one at a time.
   *
   * The hand-written version silently stopped counting configuration when slice 8b added it — the
   * totals only feed an indicator, so nothing failed, and the test that would have caught it did not
   * assert on them. A list is harder to forget to extend than an expression.
   */
  const pages = [
    outlets,
    journeys,
    configuration,
    products,
    assortment,
    outletAssortment,
    priceLists,
    priceLines,
    priceAssignments,
    promotions,
    promotionAssignments,
    surveys,
    scoreWeights,
    taxRates,
    orderMinimums,
  ];

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
  /**
   * Told about every run's result, whoever started it (`OFF-05`).
   *
   * <b>Added when the field shell mounted this for the first time (W9 slice 1), because without it
   * one of the indicator's states was unreachable.</b> A run the rep asks for reports back through
   * the promise; a run triggered by `online` had nowhere to report to — the note on `onOnline`
   * below said exactly that about failures and it was equally true of results. So a device rejected
   * during a reconnect sync left the app looking healthy while it had silently stopped pulling,
   * which is the one failure `deviceRejected` exists to make visible.
   */
  onResult?: (result: SyncResult) => void,
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
        return {
          pushed: 0,
          rejected: 0,
          pulled: 0,
          dropped: 0,
          cursor: 0,
          uploaded: 0,
          awaitingUpload: 0,
          interrupted: "unauthorized" as const,
        };
      }

      return syncOnce(db, token, deviceId);
    })()
      .then((result) => {
        // Inside the chain rather than in `finally`, so a caller cannot observe the result before
        // the observer does — the indicator and the promise describe the same run, in that order.
        // Guarded because an observer that throws must not turn a completed sync into a failed one.
        try {
          onResult?.(result);
        } catch {
          // A UI that cannot record the outcome is a UI problem; the work still reached the server.
        }

        return result;
      })
      .finally(() => {
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
