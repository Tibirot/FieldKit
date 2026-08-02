import type messages from "./messages/en.json";
import type { routing } from "./i18n/routing";

/**
 * Makes `t("Home.title")` and `useLocale()` type-safe: the English catalog is the schema, so a
 * typo or a key that only exists in one language is a compile error, not a runtime `MISSING_MESSAGE`.
 */
declare module "next-intl" {
  interface AppConfig {
    Locale: (typeof routing.locales)[number];
    Messages: typeof messages;
  }
}
