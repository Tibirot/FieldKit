import { readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";

/**
 * Rasterizes the app-icon SVGs in `public/icons` to the PNGs the manifest points at.
 *
 * **A maintenance script, not a build step.** The PNGs are committed, because they change roughly
 * never and CI has no business re-encoding brand assets on every run. Run this by hand after
 * editing `icon.svg` or `icon-maskable.svg`:
 *
 *     node scripts/generate-icons.mjs
 *
 * `sharp` is deliberately not a declared dependency here — it arrives as Next's optional
 * dependency for image optimization (and is version-pinned by the `overrides` block in
 * package.json). Adding a second declaration for a script that runs a handful of times in the
 * project's life would widen the dependency surface for nothing, so this imports it lazily and
 * says so plainly when it isn't there.
 */

const iconsDir = fileURLToPath(new URL("../public/icons/", import.meta.url));

/** The sRGB `--primary` teal — matches `BRAND.primary` in lib/pwa/manifest.ts and both SVGs. */
const TEAL = "#007B70";

const OUTPUTS = [
  { source: "icon.svg", out: "icon-192.png", size: 192 },
  { source: "icon.svg", out: "icon-512.png", size: 512 },
  { source: "icon-maskable.svg", out: "icon-maskable-512.png", size: 512 },
  // iOS ignores the manifest's icons and masks this one itself, so it must be full-bleed: flatten
  // the rounded icon's transparent corners onto the brand colour rather than ship a second SVG.
  { source: "icon.svg", out: "apple-touch-icon.png", size: 180, flatten: true },
];

async function main() {
  let sharp;
  try {
    sharp = (await import("sharp")).default;
  } catch {
    console.error(
      "sharp is not installed. It ships as Next's optional dependency — run `npm ci` in frontend/, " +
        "and check that the install did not omit optional dependencies.",
    );
    process.exitCode = 1;
    return;
  }

  for (const { source, out, size, flatten } of OUTPUTS) {
    const svg = await readFile(`${iconsDir}${source}`);
    let pipeline = sharp(svg, { density: 512 }).resize(size, size);

    if (flatten) {
      pipeline = pipeline.flatten({ background: TEAL });
    }

    await writeFile(`${iconsDir}${out}`, await pipeline.png({ compressionLevel: 9 }).toBuffer());
    console.log(`${out.padEnd(24)} ${size}×${size}  from ${source}`);
  }
}

await main();
