import "fake-indexeddb/auto";

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
  closeDatabase,
  FieldKitDatabase,
  type LocalVisit,
  type ReferenceOutlet,
  type ReferenceVisitWorkflow,
} from "@/lib/sync/db";
import { pending } from "@/lib/sync/outbox";
import { checkIn, checkOut, completeStep, inProgress, openMandatorySteps, visitsAt } from "@/lib/visits/local-visit";

/**
 * A visit worked entirely on the device (`OFF-01`, `OFF-02`) — W9 slice 4.
 *
 * The rules asserted here are the *server's* rules, re-run on a phone. That duplication is the
 * point: there is nobody else to enforce them offline, and a rep told at reconnect that their
 * check-out was invalid has been told far too late to do anything about it.
 */
const SHOP: ReferenceOutlet = {
  id: "outlet-1",
  code: "RO-BUC-0001",
  name: "Mega Image Dorobanți",
  channelId: "channel-1",
  segment: "A",
  status: "Active",
  latitude: 44.4638,
  longitude: 26.0946,
  radiusMetres: 150,
  rowVersion: 4,
};

const WORKFLOW: ReferenceVisitWorkflow = {
  id: "workflow-1",
  channelId: "channel-1",
  presenceExpected: true,
  steps: [
    { order: 1, type: "Audit", mandatory: true, label: "Shelf check" },
    { order: 2, type: "Order", mandatory: false, label: "Take an order" },
    { order: 3, type: "Note", mandatory: false, label: "Anything else" },
  ],
};

/** The rep's phone says they are at the pin. */
const AT_THE_SHOP = { latitude: 44.4638, longitude: 26.0946 };

/** Two kilometres north — outside any radius a tenant would set. */
const DOWN_THE_ROAD = { latitude: 44.4838, longitude: 26.0946 };

const NINE = new Date("2026-03-17T09:00:00.000Z");
const NINE_TEN = new Date("2026-03-17T09:10:00.000Z");
const NINE_TWENTY_FIVE = new Date("2026-03-17T09:25:00.000Z");

let db: FieldKitDatabase;

beforeEach(() => {
  db = new FieldKitDatabase(`visits:${crypto.randomUUID()}`);
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

/** Checks in at the pin with the standard workflow, for the tests that start after that. */
async function started(): Promise<LocalVisit> {
  const result = await checkIn(db, {
    outlet: SHOP,
    workflow: WORKFLOW,
    at: AT_THE_SHOP,
    now: NINE,
  });

  if (!result.ok) throw new Error(`check-in refused: ${result.refusal}`);

  return result.value;
}

describe("checking in", () => {
  it("records the device's own geofence verdict", async () => {
    const visit = await started();

    expect(visit.wasInsideGeofence).toBe(true);
    expect(visit.checkInDistanceMetres).toBe(0);
    expect(visit.overrideReason).toBeNull();

    // Copied from the workflow, in order, with ids of their own. The workflow can be republished
    // mid-visit (`BR-VIS-6`) and this rep must still be asked for what they walked in with.
    expect(visit.steps.map((step) => step.label)).toEqual([
      "Shelf check",
      "Take an order",
      "Anything else",
    ]);
    expect(new Set(visit.steps.map((step) => step.stepId)).size).toBe(3);
  });

  it("asks why, rather than refusing, when the rep is somewhere else", async () => {
    // `BR-VIS-2` never blocks. The strongest thing the rule says is "explain".
    const refused = await checkIn(db, {
      outlet: SHOP,
      workflow: WORKFLOW,
      at: DOWN_THE_ROAD,
      now: NINE,
    });

    expect(refused).toEqual({ ok: false, refusal: "visit.checkIn.overrideReasonRequired" });

    const allowed = await checkIn(db, {
      outlet: SHOP,
      workflow: WORKFLOW,
      at: DOWN_THE_ROAD,
      overrideReason: "Owner asked me to meet him at the depot",
      now: NINE,
    });

    expect(allowed.ok).toBe(true);
    expect(allowed.ok && allowed.value.wasInsideGeofence).toBe(false);
    expect(allowed.ok && allowed.value.checkInDistanceMetres).toBeGreaterThan(2000);
  });

  it("does not keep a reason nobody asked for", async () => {
    // Volunteered from inside the fence, it would be noise on a supervisor's screen — and would make
    // "how many overrides this month" a count of typing rather than of exceptions.
    const result = await checkIn(db, {
      outlet: SHOP,
      workflow: WORKFLOW,
      at: AT_THE_SHOP,
      overrideReason: "Just felt like explaining myself",
      now: NINE,
    });

    expect(result.ok && result.value.overrideReason).toBeNull();
  });

  it("refuses a second visit while one is open", async () => {
    // `BR-VIS-1` on the device. Two open visits are two shops at once — and a step completion with
    // no unambiguous visit to attach to.
    await started();

    const second = await checkIn(db, {
      outlet: SHOP,
      workflow: WORKFLOW,
      at: AT_THE_SHOP,
      now: NINE_TEN,
    });

    expect(second).toEqual({ ok: false, refusal: "visit.checkIn.alreadyInProgress" });
  });

  it("works a channel nobody has configured", async () => {
    // No steps and presence expected: a real visit — check in, check out — and not a broken one.
    const result = await checkIn(db, {
      outlet: SHOP,
      workflow: undefined,
      at: AT_THE_SHOP,
      now: NINE,
    });

    expect(result.ok && result.value.steps).toEqual([]);
    expect(result.ok && result.value.wasInsideGeofence).toBe(true);
  });

  it("asks nothing about a shop nobody has placed", async () => {
    // Making a rep justify a gap in master data would blame them for it.
    const result = await checkIn(db, {
      outlet: { ...SHOP, latitude: null, longitude: null },
      workflow: WORKFLOW,
      at: AT_THE_SHOP,
      now: NINE,
    });

    expect(result.ok).toBe(true);
    expect(result.ok && result.value.checkInDistanceMetres).toBeNull();
    expect(result.ok && result.value.overrideReason).toBeNull();
  });
});

describe("working the steps", () => {
  it("stamps a step once and refuses to restamp it", async () => {
    const visit = await started();
    const audit = visit.steps[0];

    const done = await completeStep(db, visit.id, audit.stepId, { now: NINE_TEN });
    expect(done.ok && done.value.steps[0].completedAtUtc).toBe(NINE_TEN.toISOString());

    // The first completion's timestamp is a fact about the rep's day; restamping would make
    // time-on-step a measure of the last edit.
    const again = await completeStep(db, visit.id, audit.stepId, { now: NINE_TWENTY_FIVE });
    expect(again).toEqual({ ok: false, refusal: "visit.step.notOpen" });
  });

  it("will not tick a note step with nothing written", async () => {
    const visit = await started();
    const note = visit.steps[2];

    expect(await completeStep(db, visit.id, note.stepId, { notes: "   ", now: NINE_TEN })).toEqual({
      ok: false,
      refusal: "visit.step.noteRequired",
    });

    const written = await completeStep(db, visit.id, note.stepId, {
      notes: "  Reordered two cases.  ",
      now: NINE_TEN,
    });

    expect(written.ok && written.value.steps[2].notes).toBe("Reordered two cases.");
  });

  it("survives two completions racing each other", async () => {
    // The read-modify-write this function exists to make safe. Both calls read the visit, both
    // rewrite the whole `steps` array — without a transaction the later write is computed from a
    // snapshot taken before the earlier one landed, and one of the two steps comes back undone.
    const visit = await started();

    await Promise.all([
      completeStep(db, visit.id, visit.steps[0].stepId, { now: NINE_TEN }),
      completeStep(db, visit.id, visit.steps[1].stepId, { now: NINE_TEN }),
    ]);

    const stored = (await db.visits.get(visit.id))!;

    expect(stored.steps.filter((step) => step.completedAtUtc !== null)).toHaveLength(2);
  });
});

describe("checking out", () => {
  it("is refused while a mandatory step is open, and names nothing else", async () => {
    const visit = await started();

    expect(openMandatorySteps(visit).map((step) => step.label)).toEqual(["Shelf check"]);

    expect(await checkOut(db, visit.id, { outcome: "Productive", now: NINE_TWENTY_FIVE })).toEqual({
      ok: false,
      refusal: "visit.checkOut.mandatoryStepsOpen",
    });
  });

  it("needs a reason for a non-productive call", async () => {
    const visit = await started();
    await completeStep(db, visit.id, visit.steps[0].stepId, { now: NINE_TEN });

    expect(
      await checkOut(db, visit.id, { outcome: "NonProductive", now: NINE_TWENTY_FIVE }),
    ).toEqual({ ok: false, refusal: "visit.checkOut.reasonRequired" });

    const withReason = await checkOut(db, visit.id, {
      outcome: "NonProductive",
      reason: "Closed for refurbishment",
      now: NINE_TWENTY_FIVE,
    });

    expect(withReason.ok && withReason.value.outcomeReason).toBe("Closed for refurbishment");
  });

  it("seals the visit and queues exactly one mutation", async () => {
    const visit = await started();
    await completeStep(db, visit.id, visit.steps[0].stepId, { now: NINE_TEN });
    await completeStep(db, visit.id, visit.steps[2].stepId, {
      notes: "Reordered two cases.",
      now: NINE_TEN,
    });

    const sealed = await checkOut(db, visit.id, {
      outcome: "Productive",
      at: AT_THE_SHOP,
      now: NINE_TWENTY_FIVE,
    });

    expect(sealed.ok && sealed.value.status).toBe("checkedOut");

    const queued = await pending(db);
    expect(queued).toHaveLength(1);
    expect(queued[0].type).toBe("CapturedVisit");

    // The subject is the *visit*, so `SyncBadge` on a row in the rep's day can ask about it.
    expect(queued[0].subjectId).toBe(visit.id);

    const payload = queued[0].payload as Record<string, unknown>;

    // The server's field names, because the local shape was the wire shape all along.
    expect(payload.visitId).toBe(visit.id);
    expect(payload.checkedInAtUtc).toBe(NINE.toISOString());
    expect(payload.checkedOutAtUtc).toBe(NINE_TWENTY_FIVE.toISOString());
    expect(payload.wasInsideGeofence).toBe(true);

    // Only the steps the rep actually did. `VisitStep.Ingested` requires a completion timestamp, so
    // an untouched optional step is an absence rather than a row with a null in it.
    expect((payload.steps as unknown[]).map((step) => (step as { label: string }).label)).toEqual([
      "Shelf check",
      "Anything else",
    ]);
  });

  it("leaves nothing behind when it refuses", async () => {
    // The half-state a two-write check-out would produce: a visit marked finished with nothing
    // queued. Asserted on the refusal path because that is where an early `put` would show.
    const visit = await started();

    await checkOut(db, visit.id, { outcome: "Productive", now: NINE_TWENTY_FIVE });

    expect((await db.visits.get(visit.id))!.status).toBe("inProgress");
    expect(await pending(db)).toEqual([]);
  });

  it("does not seal the visit if the outbox write fails", async () => {
    // **The claim the whole function is shaped around.** Sealing and queueing are two writes, and
    // between them is a window where the visit is finished and nothing will ever send it — a rep's
    // day lost in the one place they would never think to look, because their phone shows it done.
    //
    // A crash cannot be staged, but a *failing* second write proves the same property: with one
    // transaction the first write rolls back, and without one it does not. Run against a
    // non-transactional check-out this test finds the visit sealed.
    const visit = await started();
    await completeStep(db, visit.id, visit.steps[0].stepId, { now: NINE_TEN });

    const add = vi.spyOn(db.outbox, "add").mockRejectedValueOnce(new Error("quota exceeded"));

    await expect(
      checkOut(db, visit.id, { outcome: "Productive", now: NINE_TWENTY_FIVE }),
    ).rejects.toThrow();

    add.mockRestore();

    expect((await db.visits.get(visit.id))!.status).toBe("inProgress");
    expect(await pending(db)).toEqual([]);

    // And the rep can simply try again — nothing about the visit was consumed by the attempt.
    const retried = await checkOut(db, visit.id, {
      outcome: "Productive",
      now: NINE_TWENTY_FIVE,
    });

    expect(retried.ok).toBe(true);
    expect(await pending(db)).toHaveLength(1);
  });

  it("cannot be done twice", async () => {
    const visit = await started();
    await completeStep(db, visit.id, visit.steps[0].stepId, { now: NINE_TEN });
    await checkOut(db, visit.id, { outcome: "Productive", now: NINE_TWENTY_FIVE });

    expect(await checkOut(db, visit.id, { outcome: "Productive", now: NINE_TWENTY_FIVE })).toEqual({
      ok: false,
      refusal: "visit.notInProgress",
    });

    // And the day's work reached the outbox exactly once.
    expect(await pending(db)).toHaveLength(1);
  });

  it("frees the device for the next shop", async () => {
    const visit = await started();
    await completeStep(db, visit.id, visit.steps[0].stepId, { now: NINE_TEN });
    await checkOut(db, visit.id, { outcome: "Productive", now: NINE_TWENTY_FIVE });

    expect(await inProgress(db)).toBeUndefined();

    const next = await checkIn(db, {
      outlet: SHOP,
      workflow: WORKFLOW,
      at: AT_THE_SHOP,
      now: NINE_TWENTY_FIVE,
    });

    expect(next.ok).toBe(true);
  });

  it("keeps the visit on the device after it is queued", async () => {
    // Deleting it would make a rep's own day vanish the moment they finished it — and leave
    // `SyncBadge` with a subject id and nothing to point at.
    const visit = await started();
    await completeStep(db, visit.id, visit.steps[0].stepId, { now: NINE_TEN });
    await checkOut(db, visit.id, { outcome: "Productive", now: NINE_TWENTY_FIVE });

    expect(await visitsAt(db, SHOP.id)).toHaveLength(1);
  });
});

describe("the store itself", () => {
  it("survives being closed and reopened", async () => {
    // `OFF-02`, and the reason this store exists at all: a visit half-worked when a phone dies is
    // the one thing here the next sync cannot rebuild.
    const name = `visits:${crypto.randomUUID()}`;
    const first = new FieldKitDatabase(name);

    const result = await checkIn(first, {
      outlet: SHOP,
      workflow: WORKFLOW,
      at: AT_THE_SHOP,
      now: NINE,
    });

    if (!result.ok) throw new Error(result.refusal);
    await completeStep(first, result.value.id, result.value.steps[0].stepId, { now: NINE_TEN });
    first.close();

    const reopened = new FieldKitDatabase(name);
    const recovered = await inProgress(reopened);

    expect(recovered?.id).toBe(result.value.id);
    expect(recovered?.steps[0].completedAtUtc).toBe(NINE_TEN.toISOString());

    await reopened.delete();
  });
});
