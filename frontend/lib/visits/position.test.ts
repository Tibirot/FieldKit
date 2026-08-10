import { afterEach, describe, expect, it, vi } from "vitest";

import { currentPosition } from "@/lib/visits/position";

/**
 * The device's own fix (`VIS-01`) — W9 slice 6.
 *
 * What is worth pinning here is the *shape of failure*, not the happy path: a rep with location
 * blocked, a phone with no fix, and a browser that has no geolocation at all are three different
 * sentences, and every one of them still has to let the visit start (`BR-VIS-2`).
 */
function withGeolocation(implementation: Partial<Geolocation>) {
  const original = Object.getOwnPropertyDescriptor(globalThis.navigator, "geolocation");

  Object.defineProperty(globalThis.navigator, "geolocation", {
    value: implementation,
    configurable: true,
  });

  return () => {
    if (original) Object.defineProperty(globalThis.navigator, "geolocation", original);
    else Reflect.deleteProperty(globalThis.navigator as object, "geolocation");
  };
}

let restore: (() => void) | undefined;

afterEach(() => {
  restore?.();
  restore = undefined;
});

describe("asking the device where it is", () => {
  it("answers with the coordinates and how good the fix is", async () => {
    restore = withGeolocation({
      getCurrentPosition: (onSuccess) =>
        onSuccess({
          coords: { latitude: 44.4638, longitude: 26.0946, accuracy: 12.4 },
          timestamp: 0,
        } as GeolocationPosition),
    });

    const outcome = await currentPosition();

    expect(outcome).toEqual({
      ok: true,
      at: { latitude: 44.4638, longitude: 26.0946 },
      accuracyMetres: 12.4,
    });
  });

  it("never asks for a cached fix, because the last one was the last shop", async () => {
    // `maximumAge: 0` is the whole of it. A browser handed a larger value would return the position
    // it took in the previous shop's car park, and the geofence would agree with it — a check-in
    // recorded somewhere the rep is not, with nothing anywhere to show it was wrong.
    const getCurrentPosition = vi.fn<Geolocation["getCurrentPosition"]>((onSuccess) =>
      onSuccess({
        coords: { latitude: 1, longitude: 2, accuracy: 5 },
        timestamp: 0,
      } as GeolocationPosition),
    );

    restore = withGeolocation({ getCurrentPosition });

    await currentPosition();

    expect(getCurrentPosition.mock.calls[0][2]).toMatchObject({
      maximumAge: 0,
      enableHighAccuracy: true,
    });
  });

  it("names the three ways a phone says no, because a rep can act on two of them", async () => {
    for (const [code, problem] of [
      [1, "denied"],
      [2, "unavailable"],
      [3, "timeout"],
    ] as const) {
      restore?.();
      restore = withGeolocation({
        getCurrentPosition: (_onSuccess, onError) =>
          onError?.({ code, message: "" } as GeolocationPositionError),
      });

      expect(await currentPosition()).toEqual({ ok: false, problem });
    }
  });

  it("resolves rather than throwing when the browser has no geolocation at all", async () => {
    // An old browser, or a page served over plain http. Rejecting would make the caller wrap every
    // check-in in a try/catch to reach a state that is not exceptional.
    restore = withGeolocation(undefined as unknown as Geolocation);

    expect(await currentPosition()).toEqual({ ok: false, problem: "unsupported" });
  });

  it("reports no accuracy rather than a nonsense one when the platform does not give a number", async () => {
    restore = withGeolocation({
      getCurrentPosition: (onSuccess) =>
        onSuccess({
          coords: { latitude: 1, longitude: 2, accuracy: Number.NaN },
          timestamp: 0,
        } as GeolocationPosition),
    });

    const outcome = await currentPosition();

    expect(outcome).toMatchObject({ ok: true, accuracyMetres: null });
  });
});
