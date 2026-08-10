import type { GeoPoint } from "@/lib/visits/geofencing";

/**
 * Where the device thinks it is (`VIS-01`) — W9 slice 6.
 *
 * <b>A promise around `navigator.geolocation`, and the wrapper earns its place three times.</b> The
 * browser API is callback-shaped, it has no timeout that fires reliably on every platform, and it
 * reports "no position" through an error object whose `code` a screen would otherwise have to know
 * about. What a check-in wants is one value it can branch on, and one that can be handed to a test
 * without a global stub in every case.
 *
 * <b>Failure is not exceptional here.</b> A rep in a basement stockroom, a phone with location
 * turned off, a browser the rep denied once six months ago — these are ordinary, and `BR-VIS-2` is
 * emphatic that none of them keeps a rep out of a shop. So this never rejects: it answers with
 * *why*, and the caller turns that into a sentence and a reason box.
 */

/** Why the device could not say where it is. Each is a different sentence to a rep. */
export type PositionProblem =
  /** No `navigator.geolocation` at all — an old browser, or a non-secure origin. */
  | "unsupported"
  /** The rep, or the platform, said no. The only one they can fix from settings. */
  | "denied"
  /** The hardware tried and failed: indoors, airplane mode, no fix. */
  | "unavailable"
  /** It took longer than we were willing to keep the rep standing there. */
  | "timeout";

export type PositionOutcome =
  | {
      ok: true;
      at: GeoPoint;
      /**
       * The radius the browser is confident to, in metres, or `null` when it does not say.
       *
       * <b>Shown to the rep and not stored.</b> A check-in forty metres outside a hundred-and-fifty
       * metre fence means something different when the fix is good to five metres than when it is
       * good to eighty, and that is the rep's judgement to make before they type a reason. It is
       * *not* on the record because `CapturedVisit` is a public contract — adding a field to it is a
       * server change, and one worth making deliberately rather than as a side effect of a screen.
       */
      accuracyMetres: number | null;
    }
  | { ok: false; problem: PositionProblem };

/**
 * Asks the device for a fix.
 *
 * `maximumAge: 0` on purpose: a cached position is exactly the wrong thing here. The browser would
 * happily hand back the fix it took in the car park of the last shop, and a check-in recorded
 * against it would say the rep was somewhere they no longer are — silently, and with the geofence
 * agreeing.
 */
export function currentPosition(
  options: { timeoutMs?: number } = {},
): Promise<PositionOutcome> {
  const geolocation = globalThis.navigator?.geolocation;
  if (!geolocation) return Promise.resolve({ ok: false, problem: "unsupported" });

  return new Promise((resolve) => {
    geolocation.getCurrentPosition(
      (position) =>
        resolve({
          ok: true,
          at: {
            latitude: position.coords.latitude,
            longitude: position.coords.longitude,
          },
          accuracyMetres: Number.isFinite(position.coords.accuracy)
            ? position.coords.accuracy
            : null,
        }),
      (error) => resolve({ ok: false, problem: problemOf(error) }),
      {
        // Worth the battery: the whole point of the fix is deciding a hundred-and-fifty metre
        // question, and a coarse network-derived position is routinely wrong by more than that.
        enableHighAccuracy: true,
        timeout: options.timeoutMs ?? 15_000,
        maximumAge: 0,
      },
    );
  });
}

/**
 * The browser's error code, named.
 *
 * The numeric constants are read off the error object rather than off `GeolocationPositionError`,
 * which does not exist as a global in every runtime this code is tested in — and the values are
 * fixed by the specification, so there is nothing to drift.
 */
function problemOf(error: GeolocationPositionError): PositionProblem {
  if (error.code === 1) return "denied";
  if (error.code === 3) return "timeout";

  return "unavailable";
}
