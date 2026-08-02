import type { MetadataRoute } from "next";

import type { Locale } from "@/i18n/routing";

/**
 * The FieldKit brand colours, as sRGB hex.
 *
 * The design tokens in `app/globals.css` are authored in `oklch()`, which the web-app-manifest
 * spec does not accept — `theme_color`/`background_color` must be CSS *colour keywords or
 * hex/rgb values*. These are the exact sRGB conversions of the corresponding tokens, so the
 * splash screen matches the app it launches. `--primary` is marginally outside the sRGB gamut
 * and clips to #007B70, which is what a browser renders for it anyway.
 */
export const BRAND = {
  /** `--primary` (light) — the teal accent. */
  primary: "#007B70",
  /** `--background` (light). */
  backgroundLight: "#FFFFFF",
  /** `--background` (dark). */
  backgroundDark: "#080E13",
} as const;

/** The translated strings a manifest needs — kept as a plain object so this module stays pure. */
export type ManifestStrings = {
  name: string;
  shortName: string;
  description: string;
};

/**
 * Builds the web-app manifest for one locale (OFF-10, ADR-0004).
 *
 * FieldKit serves **one manifest per locale** rather than a single shared one, because a manifest
 * carries user-visible text (`name`, `description`) and a launch URL — none of which can be
 * locale-neutral under the always-prefixed routing that `i18n/routing.ts` mandates. Installing
 * from `/ro` therefore gives a Romanian home-screen entry that launches into Romanian.
 *
 * Two deliberate choices:
 *
 * - **`id` is `/`, not the locale.** The identity is one app, not one app per language, so
 *   installing from a second locale *re-points* the existing installation instead of adding a
 *   duplicate icon to the home screen.
 * - **`scope` is `/`, not `/{locale}`.** The locale switcher navigates across prefixes; a
 *   locale-scoped app would eject to a browser tab the moment a rep changed language.
 */
export function buildManifest(locale: Locale, strings: ManifestStrings): MetadataRoute.Manifest {
  return {
    id: "/",
    name: strings.name,
    short_name: strings.shortName,
    description: strings.description,
    lang: locale,
    dir: "ltr",
    start_url: `/${locale}`,
    scope: "/",
    display: "standalone",
    // Field reps work one-handed on a phone in a store aisle; the back office is a browser tab.
    orientation: "portrait",
    background_color: BRAND.backgroundLight,
    theme_color: BRAND.primary,
    categories: ["business", "productivity"],
    icons: [
      // Source of truth for all four is `public/icons/icon.svg` (rounded) and
      // `public/icons/icon-maskable.svg` (full-bleed) — see `scripts/generate-icons.mjs`.
      { src: "/icons/icon-192.png", sizes: "192x192", type: "image/png", purpose: "any" },
      { src: "/icons/icon-512.png", sizes: "512x512", type: "image/png", purpose: "any" },
      // A separate full-bleed art asset: Android crops "any" icons to its own mask, so a rounded
      // icon reused here loses its corners.
      { src: "/icons/icon-maskable-512.png", sizes: "512x512", type: "image/png", purpose: "maskable" },
    ],
  };
}
