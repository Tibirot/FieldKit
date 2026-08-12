import "fake-indexeddb/auto";

import { afterEach, beforeEach, describe, expect, it } from "vitest";

import {
  auditFor,
  clearAvailability,
  draft,
  draftFor,
  putAvailability,
  seal,
} from "@/lib/audits/local-audit";
import { closeDatabase, FieldKitDatabase } from "@/lib/sync/db";

/**
 * The audit as the device holds it (`AUD-01`, `BR-AUD-6`, `OFF-01b`) — W11 slice 9a.
 *
 * Against a real database, because every claim here is about a transaction: that a draft survives,
 * that an answer replaces rather than appends, and that sealing writes the audit and its outbox row
 * or neither.
 */
const NOW = new Date("2026-03-17T10:15:00.000Z");
const LATER = new Date("2026-03-17T10:20:00.000Z");

const REQUEST = {
  visitId: "visit-1",
  outletId: "outlet-1",
  weightSetVersion: 3,
  now: NOW,
};

let db: FieldKitDatabase;

beforeEach(() => {
  db = new FieldKitDatabase(`audit:${crypto.randomUUID()}`);
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

describe("starting an audit", () => {
  it("records the weighting it will be scored against", async () => {
    // `BR-AUD-8`, and the one fact that cannot be recovered later: a re-weighting between the shelf
    // and the push would leave the server unable to say which numbers the rep saw.
    const started = await draftFor(db, REQUEST);

    expect(started.weightSetVersion).toBe(3);
    expect(started.status).toBe("draft");
    expect(started.capturedAtUtc).toBeNull();
  });

  it("returns the existing draft rather than starting a second", async () => {
    // One audit per visit, which is the aggregate's rule server-side (`AlreadyAudited`). A second
    // draft would be the device inventing a conflict the server would then have to refuse.
    const first = await draftFor(db, REQUEST);
    const second = await draftFor(db, { ...REQUEST, weightSetVersion: 9, now: LATER });

    expect(second.id).toBe(first.id);
    expect(second.weightSetVersion).toBe(3);
    expect(await db.audits.count()).toBe(1);
  });
});

describe("answering for a product", () => {
  it("replaces an earlier answer rather than appending", async () => {
    /*
     * A rep who taps *Absent* and then finds the case behind a display has corrected themselves, not
     * measured twice — and the server refuses a product appearing twice in one section
     * (`DuplicateProduct`), so an append would build an audit that cannot be sent.
     */
    const started = await draftFor(db, REQUEST);

    await putAvailability(db, started.id, "p-1", "Absent", NOW);
    const corrected = await putAvailability(db, started.id, "p-1", "Present", LATER);

    expect(corrected?.availability).toEqual([{ productId: "p-1", status: "Present" }]);
  });

  it("un-answers a line rather than making the rep pick a value they do not mean", async () => {
    // All three answers are assertions about the shelf. A rep who tapped the wrong row needs a way
    // back to having said nothing, and an unanswered line is simply not sent.
    const started = await draftFor(db, REQUEST);

    await putAvailability(db, started.id, "p-1", "OutOfStock", NOW);
    const cleared = await clearAvailability(db, started.id, "p-1", LATER);

    expect(cleared?.availability).toEqual([]);
  });

  it("refuses a sealed audit rather than throwing", async () => {
    // `BR-AUD-6` is append-only after sync. A screen a moment behind the store should be told no.
    const started = await draftFor(db, REQUEST);
    await putAvailability(db, started.id, "p-1", "Present", NOW);
    await seal(db, started.id, LATER);

    expect(await putAvailability(db, started.id, "p-2", "Present", LATER)).toBeUndefined();
    expect((await auditFor(db, "visit-1"))?.availability).toHaveLength(1);
  });
});

describe("sealing", () => {
  it("writes the audit and its outbox row in one transaction", async () => {
    /*
     * The two are one fact. Split, a crash produces either an audit the rep believes was sent and
     * never was, or a mutation for an audit still showing as editable.
     */
    const started = await draftFor(db, REQUEST);
    await putAvailability(db, started.id, "p-1", "Present", NOW);

    const sealed = await seal(db, started.id, LATER);

    expect(sealed?.status).toBe("sealed");
    expect(sealed?.capturedAtUtc).toBe(LATER.toISOString());

    const queued = (await db.outbox.toArray())[0];

    expect(queued.type).toBe("CapturedAudit");
    expect(queued.subjectId).toBe(started.id);
    expect(queued.status).toBe("pending");
  });

  it("sends the shape the server reads, with the status as a name", async () => {
    /*
     * `AvailabilityStatus` carries `JsonStringEnumConverter` on the enum itself, so the server reads
     * `"Present"` and not `0`. An ordinal here would be read as `Present` whatever the rep tapped —
     * silent, and wrong in the direction that flatters the shop.
     *
     * `categoryFacings` is null, which `BR-AUD-2` reads as *not captured*: the share-of-shelf pillar
     * is skipped rather than scored zero, which is right for an audit that never offered to count it.
     */
    const started = await draftFor(db, REQUEST);
    await putAvailability(db, started.id, "p-1", "OutOfStock", NOW);

    await seal(db, started.id, LATER);

    expect((await db.outbox.toArray())[0].payload).toEqual({
      auditId: started.id,
      visitId: "visit-1",
      capturedAtUtc: LATER.toISOString(),
      weightSetVersion: 3,
      categoryFacings: null,
      availability: [{ productId: "p-1", status: "OutOfStock" }],
      facings: [],
      prices: [],
    });
  });

  it("refuses an audit that measured nothing", async () => {
    // The server refuses it too (`Empty`). Letting it reach the wire would cost a rep a round trip —
    // and a `failed` outbox row nothing retries — to be told something the device already knew.
    const started = await draftFor(db, REQUEST);

    expect(await seal(db, started.id, LATER)).toBeUndefined();
    expect(await db.outbox.count()).toBe(0);
    expect((await auditFor(db, "visit-1"))?.status).toBe("draft");
  });

  it("refuses to seal twice", async () => {
    const started = await draftFor(db, REQUEST);
    await putAvailability(db, started.id, "p-1", "Present", NOW);
    await seal(db, started.id, LATER);

    expect(await seal(db, started.id, LATER)).toBeUndefined();
    expect(await db.outbox.count()).toBe(1);
  });

  it("stops answering `draft` once sealed, while still answering `auditFor`", async () => {
    /*
     * The distinction `orderFor` was written for. "What may still be edited" has to go quiet at the
     * seal — that is `BR-AUD-6` as the store sees it — while "show me what I sent" must not, or a
     * screen bound to the first tells a rep who has just finished that they never started.
     */
    const started = await draftFor(db, REQUEST);
    await putAvailability(db, started.id, "p-1", "Present", NOW);
    await seal(db, started.id, LATER);

    expect(await draft(db, "visit-1")).toBeUndefined();
    expect((await auditFor(db, "visit-1"))?.id).toBe(started.id);
  });
});
