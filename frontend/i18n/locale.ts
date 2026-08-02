import { notFound } from "next/navigation";
import { hasLocale } from "next-intl";

import { routing, type Locale } from "./routing";

/**
 * Narrows the `[locale]` route param to a supported locale.
 *
 * The segment is user-controlled, so `/xx/outlets` must 404 rather than silently fall back to
 * English. Next generates the route props with `locale: string`, so every locale route funnels
 * through here to get the typed `Locale` that next-intl's APIs expect.
 */
export function resolveLocale(locale: string): Locale {
  if (!hasLocale(routing.locales, locale)) {
    notFound();
  }

  return locale;
}
