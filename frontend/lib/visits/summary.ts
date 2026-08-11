import type { LocalVisit, LocalVisitStep } from "@/lib/sync/db";

/**
 * The two facts a visit recap is made of (`VIS-09`) — W9 slice 10.
 *
 * <b>Here rather than in the component</b> because one of them is now derived in three places — the
 * sealed record on the visit screen, this recap, and `Visit.TimeOnSite` server-side — and three
 * copies of a subtraction is how two of them end up disagreeing about whether to floor or round.
 */

/**
 * Whole minutes between check-in and now, or between check-in and check-out once sealed.
 *
 * <b>Floored, matching the server</b> (`BR-VIS-5`): "18 minutes" that is really 18.4 is fine, and
 * "19" is a number nobody's watch agrees with. Never stored at either end — a stored copy is a
 * second answer that can disagree with the first.
 *
 * <b>Clamped at zero.</b> A device whose clock moved backwards between check-in and check-out — a
 * network time sync mid-visit is the ordinary cause — would otherwise produce a negative duration,
 * and "-3 minutes in the shop" is worse than the small lie of zero.
 */
export function minutesOnSite(visit: LocalVisit, now: Date): number {
  const from = Date.parse(visit.checkedInAtUtc);
  const to = visit.checkedOutAtUtc ? Date.parse(visit.checkedOutAtUtc) : now.getTime();

  return Math.max(0, Math.floor((to - from) / 60_000));
}

/**
 * The **optional** steps still open.
 *
 * Mandatory ones are deliberately absent: `BR-VIS-3` already blocks check-out on those and the
 * check-out panel names them, so listing them here too would put the same fact in two places and
 * invite a rep to wonder why the lists differ.
 *
 * These are the ones nothing stops a rep leaving behind — which is exactly why they are worth
 * showing at the moment leaving becomes irreversible.
 */
export function unfinished(visit: LocalVisit): LocalVisitStep[] {
  return visit.steps.filter((step) => !step.mandatory && step.completedAtUtc === null);
}
