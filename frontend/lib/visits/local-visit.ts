import type {
  FieldKitDatabase,
  LocalVisit,
  LocalVisitStep,
  ReferenceOutlet,
  ReferenceVisitWorkflow,
} from "@/lib/sync/db";
import { assess, type GeoPoint } from "@/lib/visits/geofencing";

/**
 * A visit, worked on the device (`OFF-01`, `VIS-01`…`VIS-05`) — W9 slice 4.
 *
 * <b>The whole lifecycle happens here, offline, and reaches the server as one mutation.</b> Check-in
 * creates it, the steps mutate it, check-out seals it and puts a `CapturedVisit` in the outbox. The
 * server sees a completed visit and never a half-worked one — which is what lets `IVisitIngest` be
 * an ingest rather than a second implementation of the check-in flow.
 *
 * <b>Every rule the server enforces is enforced here too</b>, because on a device there is nobody
 * else to enforce it: the geofence (`VIS-01`), mandatory-step gating (`BR-VIS-3`), and the reason a
 * non-productive visit owes (`VIS-05`). The server still checks — a device is not a trust boundary —
 * but a rep with no signal has to be told *now*, and being told at reconnect is being told too late.
 *
 * <b>Time arrives as an argument.</b> Every function here takes `now`, the way the C# side takes
 * `IClock`: a rep's day is a sequence of timestamps that tests need to control, and reaching for
 * `Date.now()` inside would make every assertion about ordering a race.
 */

/** Why the device refused, in the same shape the server's refusals take (ADR-0012). */
export type VisitRefusal =
  /** A visit is already open on this device. `BR-VIS-1`: one rep, one shop, one visit. */
  | "visit.checkIn.alreadyInProgress"
  /** The rep is not at the outlet and has not said why (`BR-VIS-2`). */
  | "visit.checkIn.overrideReasonRequired"
  /** No visit with that id, or it is already sealed. */
  | "visit.notInProgress"
  /** A step that is not on this visit, or is already done. */
  | "visit.step.notOpen"
  /** A `Note` step ticked with nothing written (`VIS-06`). */
  | "visit.step.noteRequired"
  /** Mandatory steps are still open (`BR-VIS-3`). */
  | "visit.checkOut.mandatoryStepsOpen"
  /** A non-productive visit with no reason (`VIS-05`). */
  | "visit.checkOut.reasonRequired";

/** What happened, in a shape a screen can branch on without exceptions. */
export type VisitResult<T> = { ok: true; value: T } | { ok: false; refusal: VisitRefusal };

const ok = <T,>(value: T): VisitResult<T> => ({ ok: true, value });
const no = <T,>(refusal: VisitRefusal): VisitResult<T> => ({ ok: false, refusal });

/**
 * Starts a visit (`VIS-01`, `VIS-02`).
 *
 * The geofence is assessed **here**, from the outlet's own pulled radius and its channel's presence
 * policy, and the verdict is stored as fact — the server keeps it unmodified, so this is the only
 * moment it is ever decided.
 *
 * `overrideReason` is kept only when the assessment actually asked for one. A reason volunteered for
 * a check-in that was inside the fence is noise on a supervisor's screen, and it would make "how
 * many overrides this month" a count of typing rather than of exceptions — the same rule
 * `Visit.CheckIn` applies server-side.
 */
export async function checkIn(
  db: FieldKitDatabase,
  request: {
    outlet: ReferenceOutlet;
    workflow: ReferenceVisitWorkflow | undefined;
    at: GeoPoint | null;
    plannedVisitId?: string | null;
    overrideReason?: string | null;
    now: Date;
  },
): Promise<VisitResult<LocalVisit>> {
  const { outlet, workflow, at, now } = request;

  // `BR-VIS-1` on the device. Two open visits would mean two shops at once, and — worse — a step
  // completion with no unambiguous visit to attach to.
  const open = await inProgress(db);
  if (open) return no("visit.checkIn.alreadyInProgress");

  // A workflow is optional and its absence is a real state, not a misconfiguration: an unconfigured
  // channel means no steps and presence expected, which is the safe direction (`IVisitWorkflow`).
  const presenceExpected = workflow?.presenceExpected ?? true;

  const placed =
    outlet.latitude === null || outlet.longitude === null
      ? null
      : { latitude: outlet.latitude, longitude: outlet.longitude };

  const assessment = assess(at, placed, outlet.radiusMetres, presenceExpected);

  const reason = request.overrideReason?.trim();
  if (assessment.reasonRequired && !reason) return no("visit.checkIn.overrideReasonRequired");

  const visit: LocalVisit = {
    id: crypto.randomUUID(),
    outletId: outlet.id,
    plannedVisitId: request.plannedVisitId ?? null,
    status: "inProgress",
    checkedInAtUtc: now.toISOString(),
    checkInLatitude: at?.latitude ?? null,
    checkInLongitude: at?.longitude ?? null,
    checkInDistanceMetres: assessment.distanceMetres,
    wasInsideGeofence: assessment.inside,
    overrideReason: assessment.reasonRequired ? (reason ?? null) : null,
    steps: (workflow?.steps ?? [])
      .slice()
      .sort((left, right) => left.order - right.order)
      .map((step) => ({
        // Minted per visit, not per workflow: two visits to the same shop are two sets of steps,
        // and the server's `CapturedStep.stepId` is the identity of one rep doing one thing once.
        stepId: crypto.randomUUID(),
        order: step.order,
        type: step.type,
        mandatory: step.mandatory,
        label: step.label,
        notes: null,
        completedAtUtc: null,
      })),
    checkedOutAtUtc: null,
    checkOutLatitude: null,
    checkOutLongitude: null,
    outcome: null,
    outcomeReason: null,
  };

  await db.visits.add(visit);

  return ok(visit);
}

/**
 * Marks one step done (`VIS-03`, `VIS-06`).
 *
 * <b>Read and write in one transaction</b>, because this is a read-modify-write of a row that a
 * second tap can race. Two completions landing together on separate reads would each write the
 * whole `steps` array from its own snapshot, and the later write would silently undo the earlier —
 * a rep tapping two steps quickly would see one of them come back undone.
 */
export async function completeStep(
  db: FieldKitDatabase,
  visitId: string,
  stepId: string,
  options: { notes?: string | null; now: Date },
): Promise<VisitResult<LocalVisit>> {
  return db.transaction("rw", db.visits, async () => {
    const visit = await db.visits.get(visitId);
    if (!visit || visit.status !== "inProgress") return no<LocalVisit>("visit.notInProgress");

    const step = visit.steps.find((candidate) => candidate.stepId === stepId);

    // "Not on this visit" and "already done" are one refusal on purpose. The first completion's
    // timestamp is a fact about the rep's day; restamping it would make time-on-step a measure of
    // the last edit, and a screen has nothing different to do about the two cases.
    if (!step || step.completedAtUtc !== null) return no<LocalVisit>("visit.step.notOpen");

    const notes = options.notes?.trim();

    // A note step *is* its text, so ticking one with nothing written records that the rep visited
    // the screen rather than that they wrote anything.
    if (step.type === "Note" && !notes) return no<LocalVisit>("visit.step.noteRequired");

    const updated: LocalVisit = {
      ...visit,
      steps: visit.steps.map((candidate) =>
        candidate.stepId === stepId
          ? // `|| null`, not `?? null`: an empty string is *not* a note, and `??` kept it because
            // `""` is not nullish. Found by reading a real device store after ticking an `Audit`
            // step from the new screen (W9 slice 7) — the row said `notes: ""`, which travels to
            // the server through `captured()` as a note nobody wrote. One fact, one representation.
            { ...candidate, notes: notes || null, completedAtUtc: options.now.toISOString() }
          : candidate,
      ),
    };

    await db.visits.put(updated);

    return ok(updated);
  });
}

/** The mandatory steps still standing between the rep and the door (`BR-VIS-3`). */
export function openMandatorySteps(visit: LocalVisit): LocalVisitStep[] {
  return visit.steps.filter((step) => step.mandatory && step.completedAtUtc === null);
}

/**
 * Ends the visit and hands it to the outbox (`VIS-05`, `OFF-04`).
 *
 * <b>The seal and the enqueue are one transaction, and that is the point of the function.</b> Two
 * writes would have a window in between: a device killed there leaves a visit the rep can see,
 * marked finished, that nothing will ever send — which is precisely the "no lost work" claim the
 * local store exists to keep, failing in the one place a rep would never think to check. Dexie gives
 * real transactions across stores, so the choice is available and taking it costs nothing.
 *
 * <b>The visit stays in the store afterwards.</b> Deleting it once queued would make a rep's own day
 * disappear from their phone the moment they finished it, and leave `SyncBadge` with a subject id
 * and nothing to point at. What it is *not* is a second record of whether the server has it — that
 * is the outbox's question, and this store does not answer it.
 */
export async function checkOut(
  db: FieldKitDatabase,
  visitId: string,
  request: {
    outcome: "Productive" | "NonProductive";
    reason?: string | null;
    at?: GeoPoint | null;
    now: Date;
  },
): Promise<VisitResult<LocalVisit>> {
  return db.transaction("rw", db.visits, db.outbox, async () => {
    const visit = await db.visits.get(visitId);
    if (!visit || visit.status !== "inProgress") return no<LocalVisit>("visit.notInProgress");

    if (openMandatorySteps(visit).length > 0) {
      return no<LocalVisit>("visit.checkOut.mandatoryStepsOpen");
    }

    const reason = request.reason?.trim();

    // "Why did nothing come of it" is the reporting fact; without it a non-productive call is a row
    // nobody can act on.
    if (request.outcome === "NonProductive" && !reason) {
      return no<LocalVisit>("visit.checkOut.reasonRequired");
    }

    const sealed: LocalVisit = {
      ...visit,
      status: "checkedOut",
      checkedOutAtUtc: request.now.toISOString(),
      checkOutLatitude: request.at?.latitude ?? null,
      checkOutLongitude: request.at?.longitude ?? null,
      outcome: request.outcome,
      outcomeReason: request.outcome === "NonProductive" ? (reason ?? null) : null,
    };

    await db.visits.put(sealed);

    /*
     * Enqueued inline rather than through `enqueue`, and this is the one place that is right.
     *
     * `enqueue` opens its own write — fine everywhere else, and here it would put the outbox row
     * outside this transaction, reintroducing exactly the window the transaction exists to close.
     * The row is the same shape; what differs is who owns the commit.
     */
    await db.outbox.add({
      mutationId: crypto.randomUUID(),
      type: "CapturedVisit",
      subjectId: sealed.id,
      payload: captured(sealed),
      status: "pending",
      createdAt: request.now.getTime(),
      attempts: 0,
    });

    return ok(sealed);
  });
}

/**
 * The visit as `/sync/push` expects it.
 *
 * A projection rather than a translation, because `LocalVisit` was shaped as `CapturedVisit` in the
 * first place — the only work here is renaming `id` to `visitId` and dropping the two fields that
 * are the device's own bookkeeping.
 */
function captured(visit: LocalVisit): Record<string, unknown> {
  return {
    visitId: visit.id,
    outletId: visit.outletId,
    plannedVisitId: visit.plannedVisitId,
    checkedInAtUtc: visit.checkedInAtUtc,
    checkInLatitude: visit.checkInLatitude,
    checkInLongitude: visit.checkInLongitude,
    checkInDistanceMetres: visit.checkInDistanceMetres,
    wasInsideGeofence: visit.wasInsideGeofence,
    overrideReason: visit.overrideReason,
    steps: visit.steps
      // Only what the rep actually did. The server instantiates nothing — an unfinished optional
      // step is an absence, not a row with a null timestamp, and `VisitStep.Ingested` requires one.
      .filter((step) => step.completedAtUtc !== null)
      .map((step) => ({
        stepId: step.stepId,
        order: step.order,
        type: step.type,
        mandatory: step.mandatory,
        label: step.label,
        notes: step.notes,
        completedAtUtc: step.completedAtUtc,
      })),
    outcome: visit.outcome,
    outcomeReason: visit.outcomeReason,
    checkedOutAtUtc: visit.checkedOutAtUtc,
    checkOutLatitude: visit.checkOutLatitude,
    checkOutLongitude: visit.checkOutLongitude,
  };
}

/** The visit this device is in the middle of, if any. */
export function inProgress(db: FieldKitDatabase): Promise<LocalVisit | undefined> {
  return db.visits.where("status").equals("inProgress").first();
}

/** One visit by id, whatever state it is in. */
export function visit(db: FieldKitDatabase, id: string): Promise<LocalVisit | undefined> {
  return db.visits.get(id);
}

/** Every visit this device has worked at one shop, oldest first. */
export async function visitsAt(db: FieldKitDatabase, outletId: string): Promise<LocalVisit[]> {
  const found = await db.visits.where("outletId").equals(outletId).toArray();

  // Sorted here rather than by an index: a rep works a shop a handful of times, and an index on the
  // timestamp would be a write on every step completion to order a list this short.
  return found.sort((left, right) => left.checkedInAtUtc.localeCompare(right.checkedInAtUtc));
}
