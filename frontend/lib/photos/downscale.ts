/**
 * Making a shelf photograph small enough to carry (`OFF-08`, `B5`) — W11 slice 11.
 *
 * <b>Why the device does this at all.</b> A phone camera produces 3–8 MB per frame, a rep takes
 * several per call, and the upload happens on whatever signal a shop's back room has. `B5` fixes the
 * policy at ~1600px on the longest edge and JPEG ~0.7 — enough to read a shelf edge or a price tag,
 * about a twentieth of the bytes. Downscaling on arrival instead would mean storing the originals in
 * IndexedDB first, which is the quota the device has least of.
 */

/** `B5`'s longest edge, in pixels. */
export const MAXIMUM_EDGE = 1600;

/** `B5`'s JPEG quality. */
export const JPEG_QUALITY = 0.7;

/**
 * The size a photograph becomes, given the longest edge it is allowed.
 *
 * <b>Never upscales.</b> A picture already smaller than the limit is left exactly as it is — a rep
 * photographing a price tag close up should not have their file grown to hit a target, and a
 * re-encode that adds pixels adds only artefacts.
 *
 * <b>Rounded, and never to zero.</b> A canvas cannot be 0 wide, and an extreme aspect ratio — a
 * panorama of a gondola end, which is a real thing a rep takes — divides the short edge below one.
 */
export function fitWithin(
  width: number,
  height: number,
  maximumEdge: number = MAXIMUM_EDGE,
): { width: number; height: number } {
  const longest = Math.max(width, height);

  if (longest <= maximumEdge) return { width, height };

  const scale = maximumEdge / longest;

  return {
    width: Math.max(1, Math.round(width * scale)),
    height: Math.max(1, Math.round(height * scale)),
  };
}

/**
 * A camera frame as a stored JPEG (`B5`).
 *
 * <b>Not unit-tested, and that is a deliberate limit rather than an oversight.</b> jsdom implements
 * no canvas, so every line below is a no-op or a throw in this suite — a test here would assert
 * against a stub and say nothing about a phone. What *is* tested is `fitWithin`, which holds the
 * arithmetic, and the store, which is handed a blob. This function is the thin part in between, and
 * it is verified in a browser.
 *
 * <b>`createImageBitmap`, not an `Image` element.</b> It decodes off the main thread, so a rep taking
 * a photograph mid-audit does not watch the shelf list freeze — and it reads EXIF orientation, which
 * an `Image` in a canvas does not: a portrait photo would otherwise be stored on its side.
 */
export async function downscale(
  file: Blob,
  maximumEdge: number = MAXIMUM_EDGE,
  quality: number = JPEG_QUALITY,
): Promise<Blob> {
  const bitmap = await createImageBitmap(file);

  try {
    const size = fitWithin(bitmap.width, bitmap.height, maximumEdge);
    const canvas = document.createElement("canvas");

    canvas.width = size.width;
    canvas.height = size.height;

    const context = canvas.getContext("2d");

    if (context === null) {
      // A browser without a 2D context cannot be worked around here, and storing the original would
      // quietly break the size budget the whole upload path assumes.
      throw new Error("This device cannot process photographs.");
    }

    context.drawImage(bitmap, 0, 0, size.width, size.height);

    return await new Promise<Blob>((resolve, reject) => {
      canvas.toBlob(
        (blob) => (blob ? resolve(blob) : reject(new Error("The photograph could not be encoded."))),
        "image/jpeg",
        quality,
      );
    });
  } finally {
    // Released whether or not the encode worked: a bitmap holds its decoded pixels off-heap, and a
    // rep takes enough photographs in a day for the leak to matter.
    bitmap.close();
  }
}
