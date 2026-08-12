import type {
  FieldKitDatabase,
  LocalAudit,
  LocalAvailabilityLine,
  LocalAvailabilityStatus,
} from "@/lib/sync/db";

/**
 * The audit a rep works at a shelf, and the moment they seal it (`AUD-01`, `OFF-01b`, `BR-AUD-6`) —
 * W11 slice 9a.
 *
 * <b>This is the store, not the screen.</b> What lives here has to be true whether or not anything
 * is rendering: a draft that survives a reload, a closed tab and an app update (`OFF-13`), and a
 * seal that puts the audit in the outbox in the same transaction that marks it sealed.
 *
 * <b>Why a draft is a device state and has no server equivalent.</b> There is no back-office
 * "enter an audit" screen and no live capture endpoint — `IAuditIngest` is the module's only write
 * path, and it takes an audit that already happened. So an audit lost before it is sealed is work
 * that existed nowhere else, which is what makes this a store rather than an unsent outbox payload.
 */

/** What a caller asks for. The device supplies the identity, the status and the clock. */
export type DraftRequest = {
  visitId: string;
  outletId: string;
  /**
   * The weighting this audit will be scored against (`BR-AUD-8`).
   *
   * Fixed when the draft starts rather than when it is sealed, and that is the whole point: a rep
   * who begins an audit before a re-weighting syncs and finishes it after must be scored on the
   * numbers they were shown. Read once, here, through `currentScoreWeightSet`; every later read goes
   * through `scoreWeightSet` with *this* version.
   */
  weightSetVersion: number;
  now: Date;
};

/**
 * The draft for a visit, starting one if there is none (`BR-AUD-6`).
 *
 * <b>At most one audit per visit</b>, which is the aggregate's rule server-side (`AlreadyAudited`)
 * and is enforced here by making "start" mean "get or start". A second draft would be the device
 * inventing a conflict the server would then have to refuse.
 */
export async function draftFor(
  db: FieldKitDatabase,
  request: DraftRequest,
): Promise<LocalAudit> {
  return db.transaction("rw", db.audits, async () => {
    const existing = await db.audits.where("visitId").equals(request.visitId).first();
    if (existing) return existing;

    const draft: LocalAudit = {
      id: crypto.randomUUID(),
      visitId: request.visitId,
      outletId: request.outletId,
      status: "draft",
      weightSetVersion: request.weightSetVersion,
      availability: [],
      capturedAtUtc: null,
      updatedAtUtc: request.now.toISOString(),
    };

    await db.audits.add(draft);

    return draft;
  });
}

/** The draft for a visit, or undefined. Never a sealed one — see {@link auditFor}. */
export function draft(
  db: FieldKitDatabase,
  visitId: string,
): Promise<LocalAudit | undefined> {
  return db.audits
    .where("visitId")
    .equals(visitId)
    .filter((candidate) => candidate.status === "draft")
    .first();
}

/**
 * The visit's audit whatever state it is in.
 *
 * A second query rather than a flag on {@link draft}, for the reason `orderFor` gives: "what may
 * still be edited" has to keep answering nothing once the audit is sealed, which is `BR-AUD-6` as
 * the store sees it — while "show me what I sent" is a different question. Conflating them makes a
 * sealed audit render as one nobody has started.
 */
export function auditFor(
  db: FieldKitDatabase,
  visitId: string,
): Promise<LocalAudit | undefined> {
  return db.audits.where("visitId").equals(visitId).first();
}

/**
 * Records how one MSL product was found, replacing any earlier answer for it (`AUD-01`).
 *
 * <b>Upsert, not append.</b> A rep who taps *Absent* and then finds the case behind a display has
 * corrected themselves, not measured twice — and the server refuses a product that appears twice in
 * one section (`DuplicateProduct`), so an append would build an audit that cannot be sent.
 *
 * Refuses a sealed audit rather than throwing: `BR-AUD-6` is append-only after sync, and a screen
 * that is a moment behind the store should be told no, not crash.
 */
export async function putAvailability(
  db: FieldKitDatabase,
  auditId: string,
  productId: string,
  status: LocalAvailabilityStatus,
  now: Date,
): Promise<LocalAudit | undefined> {
  return db.transaction("rw", db.audits, async () => {
    const current = await db.audits.get(auditId);
    if (!current || current.status !== "draft") return undefined;

    const others = current.availability.filter((line) => line.productId !== productId);

    return save(db, current, [...others, { productId, status }], now);
  });
}

/**
 * Un-answers one product.
 *
 * Worth having rather than making the rep pick a third value they do not mean: *Present*, *Absent*
 * and *OutOfStock* are all assertions about the shelf, and a rep who tapped the wrong row needs a
 * way back to having said nothing. An unanswered MSL line is simply not sent.
 */
export async function clearAvailability(
  db: FieldKitDatabase,
  auditId: string,
  productId: string,
  now: Date,
): Promise<LocalAudit | undefined> {
  return db.transaction("rw", db.audits, async () => {
    const current = await db.audits.get(auditId);
    if (!current || current.status !== "draft") return undefined;

    return save(
      db,
      current,
      current.availability.filter((line) => line.productId !== productId),
      now,
    );
  });
}

/**
 * Seals the audit and queues it (`BR-AUD-6`, `OFF-04`).
 *
 * <b>One transaction, both writes</b> — the same bargain `submit` and `checkOut` make. The audit
 * becoming sealed and the mutation existing are one fact, and splitting them leaves a window where a
 * crash produces either an audit the rep believes was sent and never was, or a mutation for an audit
 * still showing as editable.
 *
 * <b>An audit that measured nothing is refused here rather than at the server.</b> `AuditRefusal`
 * has an `Empty` for exactly this, and letting it reach the wire would cost a rep a round trip — and
 * a *failed* outbox row that nothing retries — to be told something the device already knew.
 */
export async function seal(
  db: FieldKitDatabase,
  auditId: string,
  now: Date,
): Promise<LocalAudit | undefined> {
  return db.transaction("rw", db.audits, db.outbox, async () => {
    const current = await db.audits.get(auditId);
    if (!current || current.status !== "draft" || current.availability.length === 0) {
      return undefined;
    }

    const sealed: LocalAudit = {
      ...current,
      status: "sealed",
      capturedAtUtc: now.toISOString(),
      updatedAtUtc: now.toISOString(),
    };

    await db.audits.put(sealed);

    // Enqueued inline rather than through `enqueue`, which opens its own write — that would put the
    // outbox row outside this transaction and reintroduce the window it exists to close.
    await db.outbox.add({
      mutationId: crypto.randomUUID(),
      type: "CapturedAudit",
      subjectId: sealed.id,
      payload: captured(sealed),
      status: "pending",
      createdAt: now.getTime(),
      attempts: 0,
    });

    return sealed;
  });
}

async function save(
  db: FieldKitDatabase,
  current: LocalAudit,
  availability: LocalAvailabilityLine[],
  now: Date,
): Promise<LocalAudit> {
  const updated: LocalAudit = {
    ...current,
    availability,
    updatedAtUtc: now.toISOString(),
  };

  await db.audits.put(updated);

  return updated;
}

/**
 * The audit as `/sync/push` expects it (`vectors/sync/push.v1.json`).
 *
 * <b>`status` travels as a name</b> — `AvailabilityStatus` carries `JsonStringEnumConverter` on the
 * enum itself, so the server reads `"Present"` and not `0`. The local strings are already those
 * names, which is why this is a copy rather than a mapping: a lookup table here would be a second
 * place the two vocabularies have to agree.
 *
 * <b>The three empty fields are 9b's and 9c's, and are sent explicitly rather than omitted.</b>
 * `CapturedAudit` defaults `answers` and `photos` to null but takes `facings` and `prices` as
 * required lists, so an audit with no numbers is one that measured no facings and read no prices —
 * which is exactly true today and stops being true two slices from now. `categoryFacings` is null,
 * which `BR-AUD-2` reads as *not captured*: the share-of-shelf pillar is skipped rather than scored
 * zero, and that is the right answer for an audit that never offered to count it.
 */
function captured(audit: LocalAudit): Record<string, unknown> {
  return {
    auditId: audit.id,
    visitId: audit.visitId,
    capturedAtUtc: audit.capturedAtUtc,
    weightSetVersion: audit.weightSetVersion,
    categoryFacings: null,
    availability: audit.availability.map((line) => ({
      productId: line.productId,
      status: line.status,
    })),
    facings: [],
    prices: [],
  };
}
