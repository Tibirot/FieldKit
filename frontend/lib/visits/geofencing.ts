/**
 * Whether a rep checking in is at the outlet (`VIS-01`, `VIS-02`, `BR-VIS-2`) — W9 slice 3.
 *
 * The device mirror of [`Geofencing`](../../../FieldKit.Modules.Visit/Geofencing.cs), and the third
 * rule this codebase implements twice. It has to run here because a rep standing in a shop with no
 * signal still has to be told whether they are inside the fence, and it has to *agree* with the
 * server because `IVisitIngest` stores the device's verdict **unmodified** — re-judging a visit
 * against today's radius would reclassify a rep who was legitimately inside yesterday's.
 *
 * <b>That makes disagreement worse here than in pricing.</b> A price recomputed server-side when an
 * order is placed gets a second opinion; this gets none. A device that decides "outside" writes a
 * supervisor an exception that never happened, permanently, and nothing downstream can tell.
 */

/** A position, as a device reports it or as an outlet records it. */
export type GeoPoint = { latitude: number; longitude: number };

/** What a check-in's position means, before anything is written down. */
export type GeofenceAssessment = {
  inside: boolean;
  /** How far the rep was, or `null` when that could not be worked out. */
  distanceMetres: number | null;
  reasonRequired: boolean;
};

/** Mean Earth radius, in metres — the sphere the haversine formula assumes. */
const EARTH_RADIUS_METRES = 6_371_000;

/**
 * Degrees to radians, spelled the way .NET spells it.
 *
 * `(degrees * PI) / 180`, **not** `degrees * (PI / 180)`. They are the same number in algebra and
 * not always the same `double`: the second folds `PI / 180` into a constant that is itself rounded,
 * then rounds again on the multiply. `double.DegreesToRadians` — which the C# original calls — is
 * the first form, so this is too.
 *
 * **Measured, because the temptation is to overstate it.** Across the 354 generated cases the two
 * forms give different answers on 105 of them, and the largest difference is **1.4 nanometres** —
 * three orders of magnitude below the tolerance the vectors compare with, and far too small to flip
 * a verdict given the generator's millimetre boundary guard. So this is not what keeps the two
 * languages agreeing; it is free, and it keeps them bit-identical where they can be, which makes any
 * *real* divergence stand out instead of hiding in noise that was there anyway.
 */
function toRadians(degrees: number): number {
  return (degrees * Math.PI) / 180;
}

/**
 * Assesses a check-in position.
 *
 * Three things make a reason unnecessary, and they are different in kind:
 *
 * - **The rep is inside the radius.** Nothing to explain.
 * - **The channel does not expect presence.** A phone call is legitimately not at the outlet, and
 *   demanding a reason would record an exception where nothing exceptional happened.
 * - **The outlet has no coordinates.** Nobody has placed the shop, so "were you there?" has no
 *   answer — and making a rep justify a gap in master data would blame them for it.
 *
 * The device having no fix is the one case that *does* need explaining even though nothing can be
 * measured: a phone reporting no position at a shop that has one is exactly the case a supervisor
 * would want to see, and it is also how a check-in would be faked.
 *
 * **Nothing here blocks.** `BR-VIS-2` is emphatic — outside the fence the visit still happens, it
 * just has to be explained. The strongest thing this says is `reasonRequired`.
 */
export function assess(
  at: GeoPoint | null,
  outlet: GeoPoint | null,
  radiusMetres: number,
  presenceExpected: boolean,
): GeofenceAssessment {
  if (!presenceExpected) return { inside: false, distanceMetres: null, reasonRequired: false };

  if (outlet === null) return { inside: false, distanceMetres: null, reasonRequired: false };

  if (at === null) return { inside: false, distanceMetres: null, reasonRequired: true };

  const distance = distanceMetres(at, outlet);
  const inside = distance <= radiusMetres;

  return { inside, distanceMetres: distance, reasonRequired: !inside };
}

/**
 * Great-circle distance between two points, in metres.
 *
 * Haversine on a sphere, in the same order of operations as the C# original — which is deliberate
 * and is the only lever this file has over agreement. Every step is IEEE-754 double arithmetic, so
 * `+`, `*`, `/` and `Math.sqrt` are exactly reproducible; `Math.sin`, `Math.cos` and `Math.asin` are
 * **not**. Neither language's library is correctly rounded, and they are different libraries, so two
 * correct implementations may differ in the last bit or two.
 *
 * That is why the shared vectors compare distances with a tolerance rather than for equality, and
 * why the generator keeps its cases clear of the radius boundary — see `vectors/README.md`. Matching
 * the operation order costs nothing and keeps the disagreement in the last bits rather than letting
 * it compound.
 *
 * Wrong by up to about half a percent against the real, slightly squashed Earth — twenty-odd
 * centimetres over the distances this compares, against a GPS fix routinely tens of metres out.
 */
export function distanceMetres(from: GeoPoint, to: GeoPoint): number {
  const lat1 = toRadians(from.latitude);
  const lat2 = toRadians(to.latitude);
  const deltaLat = lat2 - lat1;
  const deltaLon = toRadians(to.longitude - from.longitude);

  const a =
    Math.sin(deltaLat / 2) * Math.sin(deltaLat / 2) +
    Math.cos(lat1) * Math.cos(lat2) * Math.sin(deltaLon / 2) * Math.sin(deltaLon / 2);

  return 2 * EARTH_RADIUS_METRES * Math.asin(Math.min(1, Math.sqrt(a)));
}
