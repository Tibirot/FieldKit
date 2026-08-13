import "fake-indexeddb/auto";

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { draftFor, seal } from "@/lib/audits/local-audit";
import { attachPhoto } from "@/lib/photos/local-photo";
import {
  awaitingConfirmation,
  evidenceComplete,
  outstandingPhotographs,
  uploadPhotos,
  waiting,
} from "@/lib/photos/upload";
import { closeDatabase, FieldKitDatabase, WAITING } from "@/lib/sync/db";

/**
 * The second transport (`OFF-08`, `B5`) — W11 slice 12b.
 *
 * The presign call and the `PUT` are both mocked, and nothing else is: the store is real, the audits
 * are real, and what is under test is the *policy* — which photographs are sent, in what order, what
 * happens when one fails, and what a rep is left holding. Whether a presigned URL actually works is
 * `PhotoPresignTests`' question, answered against a real Blob service.
 */
const presigned = vi.hoisted(() => vi.fn());
const confirmed = vi.hoisted(() => vi.fn());

vi.mock("@/lib/api/sync", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/api/sync")>()),
  presignPhoto: presigned,
  confirmPhoto: confirmed,
}));

const NOW = new Date("2026-03-17T10:15:00.000Z");
const LATER = new Date("2026-03-17T11:00:00.000Z");

const REQUEST = { visitId: "visit-1", outletId: "outlet-1", weightSetVersion: 3, now: NOW };

const image = (bytes = 64) => new Blob([new Uint8Array(bytes)], { type: "image/jpeg" });

let db: FieldKitDatabase;
let put: ReturnType<typeof vi.fn>;

/** An audit with `count` photographs, sealed unless told otherwise. */
async function audited(count: number, { sealed = true } = {}) {
  const started = await draftFor(db, REQUEST);

  for (let index = 0; index < count; index += 1) {
    await attachPhoto(db, {
      auditId: started.id,
      section: "General",
      image: image(),
      photoId: `photo-${index}`,
      // Staggered, so "oldest first" is a claim the data can actually distinguish.
      now: new Date(NOW.getTime() + index * 60_000),
    });
  }

  if (sealed) await seal(db, started.id, LATER);

  return started;
}

beforeEach(() => {
  db = new FieldKitDatabase(`upload:${crypto.randomUUID()}`);

  presigned.mockReset();
  presigned.mockImplementation(async (_token: string, objectKey: string) => ({
    url: `https://storage.example/tenant/${objectKey}?sig=x`,
    objectKey: `tenant/${objectKey}`,
    expiresAtUtc: LATER.toISOString(),
  }));

  // The server's ordinary answer: it knew the key and recorded it. The "not yet" answer is its own
  // case below, because it is the one the two-transport design depends on.
  confirmed.mockReset();
  confirmed.mockResolvedValue({ confirmed: 1, unknown: 0 });

  put = vi.fn(async () => new Response(null, { status: 201 }));
  vi.stubGlobal("fetch", put);
});

afterEach(async () => {
  vi.unstubAllGlobals();
  await db.delete();
  closeDatabase();
});

describe("what gets uploaded", () => {
  it("sends a sealed audit's photographs and marks them", async () => {
    const started = await audited(2);

    const result = await uploadPhotos(db, "token", LATER);

    expect(result).toEqual({
      uploaded: 2,
      failed: 0,
      abandoned: 0,
      confirmed: 2,
      awaitingConfirmation: 0,
    });
    expect(await waiting(db)).toEqual([]);
    expect(await evidenceComplete(db, started.id)).toBe(true);
  });

  it("leaves a draft's photographs alone", async () => {
    /*
     * A draft's pictures are still the rep's to remove. Uploading one spends their data on an image
     * that may be deleted a minute later — and leaves an object in storage no audit will ever name,
     * because the reference goes with the removal.
     *
     * Not a failure, so it is not counted and not retried: the run after the seal picks it up.
     */
    await audited(1, { sealed: false });

    const result = await uploadPhotos(db, "token", LATER);

    expect(result).toEqual({
      uploaded: 0,
      failed: 0,
      abandoned: 0,
      confirmed: 0,
      awaitingConfirmation: 0,
    });
    expect(put).not.toHaveBeenCalled();
    expect(await waiting(db)).toHaveLength(1);
  });

  it("does not send the same photograph twice", async () => {
    // The index is what makes this cheap, and the second run is what proves the mark is read.
    await audited(1);
    await uploadPhotos(db, "token", LATER);

    const second = await uploadPhotos(db, "token", LATER);

    expect(second.uploaded).toBe(0);
    expect(put).toHaveBeenCalledTimes(1);
  });

  it("sends the bytes as a block blob, to the URL the server signed", async () => {
    /*
     * `x-ms-blob-type` is required by the Blob REST API and is the whole of what the SAS authorises;
     * without it the upload is refused by storage rather than by us, which is a failure a rep would
     * see as "it never works" and nobody would see as a missing header.
     */
    await audited(1);

    await uploadPhotos(db, "token", LATER);

    const [url, init] = put.mock.calls[0] as [string, RequestInit];

    expect(url).toContain("sig=x");
    expect(init.method).toBe("PUT");
    expect((init.headers as Record<string, string>)["x-ms-blob-type"]).toBe("BlockBlob");
    expect(init.body).toBeInstanceOf(Blob);
  });

  it("asks for a key without a tenant, because the device does not know its own", async () => {
    // The prefix is the server's to write (W11 slice 12a). A device that sent one would be refused,
    // and one that *derived* one would be inventing an isolation boundary.
    const started = await audited(1);

    await uploadPhotos(db, "token", LATER);

    expect(presigned).toHaveBeenCalledWith("token", `audits/${started.id}/photo-0.jpg`, undefined);
  });

  it("uploads oldest first", async () => {
    // A rep's morning should reach the back office before their afternoon, for the same reason the
    // outbox drains in order: a partial upload is likelier than a complete one on a bad connection.
    const started = await audited(3);

    await uploadPhotos(db, "token", LATER);

    const asked = presigned.mock.calls.map((call) => call[1]);

    expect(asked).toEqual([
      `audits/${started.id}/photo-0.jpg`,
      `audits/${started.id}/photo-1.jpg`,
      `audits/${started.id}/photo-2.jpg`,
    ]);
  });
});

describe("when an upload fails", () => {
  it("counts it, keeps the image, and tries the next one", async () => {
    /*
     * <b>One picture failing must not stop the others.</b> The usual cause is the connection, and a
     * loop that gave up on the first failure would leave a rep whose signal flickered with one
     * uploaded photograph out of six and no way to tell why.
     */
    put.mockImplementationOnce(async () => {
      throw new TypeError("Failed to fetch");
    });

    await audited(2);

    const result = await uploadPhotos(db, "token", LATER);

    expect(result).toEqual({
      uploaded: 1,
      failed: 1,
      abandoned: 0,
      confirmed: 1,
      awaitingConfirmation: 0,
    });

    const stuck = (await waiting(db))[0];

    expect(stuck.attempts).toBe(1);
    expect(stuck.image.size).toBe(64);
  });

  it("counts a refusal as a failure rather than treating it as done", async () => {
    // A `403` is not success. Marking it uploaded would tell a rep the back office has a photograph
    // it never received — the one lie this transport must not tell.
    put.mockImplementation(async () => new Response(null, { status: 403 }));

    await audited(1);

    const result = await uploadPhotos(db, "token", LATER);

    expect(result.failed).toBe(1);
    expect((await waiting(db))[0].uploadedAtUtc).toBe(WAITING);
  });

  it("keeps why it failed, because swallowing it hid a bug for a whole slice", async () => {
    /*
     * <b>Regression, and the reason this field exists.</b> The uploader recorded only *that* a photo
     * had failed, so a Content Security Policy refusing every `PUT` was indistinguishable from a bad
     * signal — and the retry made it look like a bad signal forever. It took a browser console to
     * find, and nothing on the device could have said.
     *
     * A message, not a stack: it is for a rep's "why is this stuck" and for whoever reads the store
     * when a photograph will not go.
     */
    put.mockImplementation(async () => {
      throw new TypeError("Refused to connect because it violates the document's CSP.");
    });

    await audited(1);
    await uploadPhotos(db, "token", LATER);

    expect((await waiting(db))[0].lastFailure).toContain("violates the document's CSP");
  });

  it("clears the reason once the photograph goes", async () => {
    // A stale explanation on a row that is fine is worse than none — slice 13 would show a rep a
    // problem they no longer have.
    put.mockImplementationOnce(async () => {
      throw new TypeError("Failed to fetch");
    });

    const started = await audited(1);
    await uploadPhotos(db, "token", LATER);
    await uploadPhotos(db, "token", LATER);

    const stored = await db.blobs.get(`audits/${started.id}/photo-0.jpg`);

    expect(stored?.lastFailure).toBe("");
    expect(stored?.uploadedAtUtc).not.toBe(WAITING);
  });

  it("stops retrying a photograph that has failed too often, without deleting it", async () => {
    /*
     * A picture that has failed eight times is failing for a reason retrying will not fix, and a rep
     * who is offline all week should not spend the first seconds of every reconnect on it.
     *
     * <b>Kept, not deleted.</b> It is the only copy of something a supervisor may still want, and
     * telling the rep is slice 13's job — throwing it away would settle the question by losing it.
     */
    const started = await audited(1);
    await db.blobs.update(`audits/${started.id}/photo-0.jpg`, { attempts: 8 });

    const result = await uploadPhotos(db, "token", LATER);

    expect(result).toEqual({
      uploaded: 0,
      failed: 0,
      abandoned: 1,
      confirmed: 0,
      awaitingConfirmation: 0,
    });
    expect(put).not.toHaveBeenCalled();
    expect(await db.blobs.count()).toBe(1);
  });

  it("clears the count when an upload finally works", async () => {
    // So a photograph that failed twice on a bad morning is not one failure closer to abandonment
    // for the rest of its life.
    const started = await audited(1);
    await db.blobs.update(`audits/${started.id}/photo-0.jpg`, { attempts: 3 });

    await uploadPhotos(db, "token", LATER);

    expect((await db.blobs.toArray())[0].attempts).toBe(0);
  });

  it("stops when the run is aborted, and keeps what it had done", async () => {
    // The manager aborts on unmount and on a second run. Half an upload pass is a normal state.
    const controller = new AbortController();

    put.mockImplementation(async () => {
      controller.abort();

      return new Response(null, { status: 201 });
    });

    await audited(3);

    const result = await uploadPhotos(db, "token", LATER, controller.signal);

    expect(result.uploaded).toBe(1);
    expect(await waiting(db)).toHaveLength(2);
  });
});

describe("whether an audit's evidence has landed", () => {
  it("is false while any photograph is still waiting", async () => {
    /*
     * The question slice 13 asks to tell *synced* from *uploaded*. An audit whose JSON the back
     * office has and whose pictures it does not is an ordinary state, not a half-failure — and a rep
     * being told "synced" while three photographs sit on the phone is the misleading version.
     */
    const started = await audited(2);
    put.mockImplementationOnce(async () => {
      throw new TypeError("Failed to fetch");
    });

    await uploadPhotos(db, "token", LATER);

    expect(await evidenceComplete(db, started.id)).toBe(false);
  });

  it("is true for an audit that took none", async () => {
    // Vacuously, and deliberately: an audit with no photographs is not waiting for any.
    const started = await draftFor(db, REQUEST);

    expect(await evidenceComplete(db, started.id)).toBe(true);
  });
});

describe("telling the server the bytes arrived", () => {
  it("confirms with the key the server minted, not the device's own", async () => {
    /*
     * <b>The one thing the device cannot reconstruct.</b> The tenant prefix comes back from presign
     * and is not ours to know, so confirming with `audits/…` would name nothing and the photograph
     * would look unacknowledged forever — the exact failure this slice exists to make impossible.
     */
    const started = await audited(1);

    await uploadPhotos(db, "token", LATER);

    expect(confirmed).toHaveBeenCalledWith(
      "token",
      `tenant/audits/${started.id}/photo-0.jpg`,
      undefined,
    );
  });

  it("confirms one key per call rather than a batch", async () => {
    /*
     * The endpoint takes a list, and using it would be wrong here: the reply is counts, so a batch
     * with one `unknown` cannot say which — and because an already-confirmed key also answers
     * `confirmed: 0`, that batch could never settle and would re-confirm every photograph in it on
     * every sync, forever.
     */
    await audited(3);

    await uploadPhotos(db, "token", LATER);

    expect(confirmed).toHaveBeenCalledTimes(3);
  });

  it("leaves a photograph unacknowledged when the audit has not landed yet", async () => {
    /*
     * <b>Ordinary, not a failure.</b> The two transports are independent and the upload can win, so
     * the server legitimately does not know the audit yet. Counting this as failed would put a
     * "needs attention" in front of a rep for something that fixes itself on the next push.
     */
    confirmed.mockResolvedValue({ confirmed: 0, unknown: 1 });

    const started = await audited(1);

    const result = await uploadPhotos(db, "token", LATER);

    expect(result.uploaded).toBe(1);
    expect(result.failed).toBe(0);
    expect(result.awaitingConfirmation).toBe(1);
    expect(await evidenceComplete(db, started.id)).toBe(false);
    expect(await awaitingConfirmation(db)).toHaveLength(1);
  });

  it("retries the confirmation on a later run without uploading again", async () => {
    /*
     * The bytes are in storage and only the sentence about them is missing. Re-uploading would spend
     * a rep's data to say something the server could be told in a hundred bytes.
     */
    confirmed.mockResolvedValue({ confirmed: 0, unknown: 1 });

    const started = await audited(1);
    await uploadPhotos(db, "token", LATER);

    confirmed.mockResolvedValue({ confirmed: 1, unknown: 0 });

    const second = await uploadPhotos(db, "token", LATER);

    expect(put).toHaveBeenCalledTimes(1);
    expect(second.confirmed).toBe(1);
    expect(await evidenceComplete(db, started.id)).toBe(true);
  });

  it("keeps the reason when the confirmation itself will not go through", async () => {
    /*
     * The `PUT` goes to storage and this goes to the API; a rep can have one and not the other. The
     * message is kept for the same reason the upload's is — W11 slice 12c shipped a feature that
     * could not work because a swallowed error looked exactly like a weak signal.
     */
    confirmed.mockRejectedValue(new Error("Failed to fetch"));

    await audited(1);

    const result = await uploadPhotos(db, "token", LATER);

    expect(result.uploaded).toBe(1);
    expect(result.awaitingConfirmation).toBe(1);
    expect((await awaitingConfirmation(db))[0].lastFailure).toBe("Failed to fetch");
  });

  it("treats an already-confirmed key as settled", async () => {
    // What a retry gets: the server knew, and said so by confirming nothing. Leaving the row
    // unacknowledged on that answer would make it retry for as long as the device lives.
    confirmed.mockResolvedValue({ confirmed: 0, unknown: 0 });

    const started = await audited(1);

    const result = await uploadPhotos(db, "token", LATER);

    expect(result.confirmed).toBe(1);
    expect(await evidenceComplete(db, started.id)).toBe(true);
  });
});

describe("what a rep is told is outstanding", () => {
  it("counts a sealed audit's unacknowledged photographs", async () => {
    confirmed.mockResolvedValue({ confirmed: 0, unknown: 1 });

    await audited(2);
    await uploadPhotos(db, "token", LATER);

    expect(await outstandingPhotographs(db)).toBe(2);
  });

  it("ignores a draft's, however many the rep has taken", async () => {
    /*
     * <b>The difference between a useful count and one a rep stops reading.</b> A draft's
     * photographs are deliberately never uploaded, so counting them would leave the indicator saying
     * *photos still to send* for as long as an audit is open — which is most of a working day.
     */
    await audited(3, { sealed: false });

    expect(await outstandingPhotographs(db)).toBe(0);
  });

  it("is zero once everything has been acknowledged", async () => {
    await audited(2);

    await uploadPhotos(db, "token", LATER);

    expect(await outstandingPhotographs(db)).toBe(0);
  });
});
