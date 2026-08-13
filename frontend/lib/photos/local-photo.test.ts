import "fake-indexeddb/auto";

import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { auditFor, draftFor, measured, seal } from "@/lib/audits/local-audit";
import { attachPhoto, objectKeyFor, photosFor, removePhoto } from "@/lib/photos/local-photo";
import { closeDatabase, FieldKitDatabase } from "@/lib/sync/db";

/**
 * Photographs on the device (`AUD-05`, `OFF-08`, `B5`) — W11 slice 11.
 *
 * Against a real database, because every claim here is about the pair of rows: the audit's reference
 * and the blob it points at, which are written and removed together or the picture is lost one way
 * or the other.
 */
const NOW = new Date("2026-03-17T10:15:00.000Z");
const LATER = new Date("2026-03-17T10:20:00.000Z");

const REQUEST = {
  visitId: "visit-1",
  outletId: "outlet-1",
  weightSetVersion: 3,
  now: NOW,
};

const image = (bytes = 1024) => new Blob([new Uint8Array(bytes)], { type: "image/jpeg" });

let db: FieldKitDatabase;

beforeEach(() => {
  db = new FieldKitDatabase(`photo:${crypto.randomUUID()}`);
});

afterEach(async () => {
  await db.delete();
  closeDatabase();
});

describe("the key a photograph is filed under", () => {
  it("puts the audit in the path, so one audit's images are one prefix", () => {
    /*
     * What makes a rejected audit's pictures findable, and what a reader needs to fetch them. The
     * extension is part of the contract too: `downscale` always encodes JPEG, and a key claiming a
     * format the object is not would mislead everything downstream that trusts it.
     */
    expect(objectKeyFor("audit-1", "photo-1")).toBe("audits/audit-1/photo-1.jpg");
  });

  it("stays well inside the column the server stores it in", () => {
    // `PhotoEntry.MaximumObjectKeyLength` is 512. Two UUIDs and a prefix is nowhere near it, and
    // this is the assertion that would notice if the shape ever grew a rep's name or a timestamp.
    expect(objectKeyFor(crypto.randomUUID(), crypto.randomUUID()).length).toBeLessThan(512);
  });
});

describe("attaching a photograph", () => {
  it("writes the image and the reference together", async () => {
    /*
     * The pair is the point. A reference with no blob is a picture that can never be uploaded; a blob
     * with no reference is one nothing will ever ask for. Either alone is a defect, so both go in one
     * transaction.
     */
    const started = await draftFor(db, REQUEST);

    const updated = await attachPhoto(db, {
      auditId: started.id,
      section: "PriceCompliance",
      image: image(2048),
      photoId: "photo-1",
      now: LATER,
    });

    const key = `audits/${started.id}/photo-1.jpg`;

    expect(updated?.photos).toEqual([{ section: "PriceCompliance", objectKey: key }]);

    const stored = await db.blobs.get(key);

    expect(stored?.auditId).toBe(started.id);
    expect(stored?.section).toBe("PriceCompliance");
    expect(stored?.bytes).toBe(2048);
    expect(stored?.capturedAtUtc).toBe(LATER.toISOString());
  });

  it("keeps every photograph, rather than replacing the last one", async () => {
    // Unlike a facings count or a price, a photograph is not a *measurement of* something the rep can
    // re-take — `AUD-05` is explicit that a section holds one or more. Two pictures of a shelf are
    // two pieces of evidence.
    const started = await draftFor(db, REQUEST);

    await attachPhoto(db, { auditId: started.id, section: "General", image: image(), photoId: "a", now: NOW });
    const updated = await attachPhoto(db, {
      auditId: started.id,
      section: "General",
      image: image(),
      photoId: "b",
      now: LATER,
    });

    expect(updated?.photos).toHaveLength(2);
    expect(await db.blobs.count()).toBe(2);
  });

  it("refuses a sealed audit rather than orphaning the blob", async () => {
    /*
     * `BR-AUD-6` is append-only after sync, and the failure mode is specific: a picture stored after
     * the push would sit in `blobs` forever, uploaded by slice 12 to a key no audit record mentions.
     * Refused whole — no reference *and* no blob.
     */
    const started = await draftFor(db, REQUEST);
    await attachPhoto(db, { auditId: started.id, section: "General", image: image(), photoId: "a", now: NOW });
    await seal(db, started.id, LATER);

    const refused = await attachPhoto(db, {
      auditId: started.id,
      section: "General",
      image: image(),
      photoId: "b",
      now: LATER,
    });

    expect(refused).toBeUndefined();
    expect(await db.blobs.count()).toBe(1);
  });
});

describe("removing a photograph", () => {
  it("takes the image with the reference", async () => {
    /*
     * Leaving the blob would upload bytes no audit mentions and no supervisor can reach — the rep
     * pays for the transfer and nobody ever sees the picture.
     */
    const started = await draftFor(db, REQUEST);
    await attachPhoto(db, { auditId: started.id, section: "General", image: image(), photoId: "a", now: NOW });

    const key = `audits/${started.id}/a.jpg`;
    const updated = await removePhoto(db, started.id, key, LATER);

    expect(updated?.photos).toEqual([]);
    expect(await db.blobs.get(key)).toBeUndefined();
  });

  it("leaves the other photographs alone", async () => {
    const started = await draftFor(db, REQUEST);
    await attachPhoto(db, { auditId: started.id, section: "General", image: image(), photoId: "a", now: NOW });
    await attachPhoto(db, { auditId: started.id, section: "Survey", image: image(), photoId: "b", now: NOW });

    const updated = await removePhoto(db, started.id, `audits/${started.id}/a.jpg`, LATER);

    expect(updated?.photos).toEqual([{ section: "Survey", objectKey: `audits/${started.id}/b.jpg` }]);
    expect(await db.blobs.count()).toBe(1);
  });
});

describe("what an audit holding only photographs is", () => {
  it("counts as measured, because the server counts it (AUD-05)", async () => {
    /*
     * `Audit.Check` says so in as many words — "an audit that is only a questionnaire, or only a
     * photograph of a display, is real work". A device that refused one would refuse an audit the
     * server would have taken, which is the shape of the bug 9b shipped and 9c repeated.
     *
     * It is also the case this exists for: a shop that will not let a rep count the shelf, where a
     * photograph of the display is the only record anyone can make.
     */
    const started = await draftFor(db, REQUEST);
    expect(measured(started)).toBe(false);

    const withPhoto = await attachPhoto(db, {
      auditId: started.id,
      section: "General",
      image: image(),
      photoId: "a",
      now: NOW,
    });

    expect(measured(withPhoto!)).toBe(true);
  });

  it("seals, and sends its references with the rest of the audit", async () => {
    const started = await draftFor(db, REQUEST);
    await attachPhoto(db, { auditId: started.id, section: "ShareOfShelf", image: image(), photoId: "a", now: NOW });

    expect(await seal(db, started.id, LATER)).toBeDefined();

    const payload = (await db.outbox.toArray())[0].payload as {
      photos: { section: string; objectKey: string }[];
    };

    // References, never images: `B5` sends the pictures separately, and the JSON push regularly wins
    // that race — so the server routinely stores a key pointing at nothing yet.
    expect(payload.photos).toEqual([
      { section: "ShareOfShelf", objectKey: `audits/${started.id}/a.jpg` },
    ]);
  });

  it("keeps the blobs after the seal, because nothing has uploaded them yet", async () => {
    /*
     * The seal is not the end of a photograph's life on this device — the upload is (`OFF-08`, slice
     * 12). Clearing them here would delete the only copy of a picture that has never left the phone.
     */
    const started = await draftFor(db, REQUEST);
    await attachPhoto(db, { auditId: started.id, section: "General", image: image(), photoId: "a", now: NOW });
    await seal(db, started.id, LATER);

    expect(await photosFor(db, started.id)).toHaveLength(1);
    expect((await auditFor(db, "visit-1"))?.status).toBe("sealed");
  });
});
