import { hasLocale } from "next-intl";
import { getRequestConfig } from "next-intl/server";

import { routing } from "./routing";

/**
 * Per-request i18n configuration, resolved by the next-intl plugin (see `next.config.ts`).
 */
export default getRequestConfig(async ({ requestLocale }) => {
  // `requestLocale` is the `[locale]` segment; it can be absent or bogus on unmatched routes.
  const requested = await requestLocale;
  const locale = hasLocale(routing.locales, requested)
    ? requested
    : routing.defaultLocale;

  return {
    locale,
    messages: (await import(`../messages/${locale}.json`)).default,

    // ADR-0010: timestamps are stored UTC and displayed in the *user's* timezone. Until IAM
    // supplies that from the profile (W3, BR-IAM-5), render in UTC — a fixed default also keeps
    // server and client formatting identical, so dates never cause a hydration mismatch.
    timeZone: "UTC",
  };
});
