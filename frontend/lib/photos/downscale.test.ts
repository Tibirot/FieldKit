import { describe, expect, it } from "vitest";

import { fitWithin, JPEG_QUALITY, MAXIMUM_EDGE } from "@/lib/photos/downscale";

/**
 * `B5`'s size policy, as arithmetic (`OFF-08`) — W11 slice 11.
 *
 * Only `fitWithin` is tested here, and deliberately: jsdom implements no canvas, so `downscale`
 * itself would be asserted against a stub that decodes nothing and encodes nothing. This function is
 * where the decisions live; the rest is `drawImage` and `toBlob`, verified in a browser.
 */
describe("fitting a photograph inside B5's budget", () => {
  it("holds the policy the whole upload path is sized against", () => {
    // Named constants rather than literals scattered through the code — and asserted, because a
    // change here changes what every device stores and what every upload costs.
    expect(MAXIMUM_EDGE).toBe(1600);
    expect(JPEG_QUALITY).toBe(0.7);
  });

  it("scales the longest edge to the limit and keeps the aspect ratio", () => {
    // A phone's 12-megapixel landscape frame: 4032×3024 is 4:3, and 1600×1200 still is.
    expect(fitWithin(4032, 3024)).toEqual({ width: 1600, height: 1200 });
  });

  it("measures the longest edge, whichever way the phone was held", () => {
    // The same frame rotated. A limit applied to `width` alone would leave a portrait photo at full
    // height — which is the orientation a rep holds a phone in to photograph a gondola end.
    expect(fitWithin(3024, 4032)).toEqual({ width: 1200, height: 1600 });
  });

  it("leaves a picture that is already small enough exactly as it is", () => {
    /*
     * Never upscales. A rep photographing a price tag close up should not have their file *grown* to
     * hit a target — the pixels a re-encode would add are artefacts, and the bytes are real.
     */
    expect(fitWithin(800, 600)).toEqual({ width: 800, height: 600 });
    expect(fitWithin(1600, 900)).toEqual({ width: 1600, height: 900 });
  });

  it("never rounds an edge away to nothing", () => {
    /*
     * A panorama of a gondola end is a real thing a rep takes, and an extreme ratio divides the short
     * edge below one. A canvas cannot be zero wide — it throws — so the photograph would be lost at
     * the moment it was taken.
     */
    expect(fitWithin(20000, 5)).toEqual({ width: 1600, height: 1 });
  });

  it("takes the limit as an argument, so a caller can say what it means by small", () => {
    // The thumbnail path a later slice may want, and the reason the constant is a default rather
    // than baked into the body.
    expect(fitWithin(4032, 3024, 320)).toEqual({ width: 320, height: 240 });
  });
});
