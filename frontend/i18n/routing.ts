import { defineRouting } from "next-intl/routing";

/**
 * Locale routing — the single source of truth for which languages FieldKit ships.
 *
 * Launch set is English + Romanian (ADR-0010 / decision A3). Adding a language is meant to be a
 * *content* task: drop `messages/<locale>.json` in and add the code here — nothing else changes.
 * The catalog-parity test (`i18n/catalogs.test.ts`) fails the build if the two drift apart.
 */
export const routing = defineRouting({
  locales: ["en", "ro"],
  defaultLocale: "en",

  // Always prefix the path (`/en/...`, `/ro/...`). Explicit URLs keep the PWA app-shell cache
  // unambiguous — a cached response can never be for the wrong language (ADR-0004).
  localePrefix: "always",
});

export type Locale = (typeof routing.locales)[number];
