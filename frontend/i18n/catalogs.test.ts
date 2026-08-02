import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import {
  isPluralElement,
  isSelectElement,
  parse,
  TYPE,
  type MessageFormatElement,
} from "@formatjs/icu-messageformat-parser";
import { describe, expect, it } from "vitest";

import { routing } from "./routing";

/**
 * The guard behind ADR-0010's claim that "adding a language is a content task": these tests fail
 * the build when a catalog drifts — a key added to one language but not the others, an empty
 * string shipped as a translation, a placeholder renamed on one side, malformed ICU, or English
 * plural rules copied into a language with different CLDR categories.
 */

type Catalog = { [key: string]: string | Catalog };

const messagesDir = fileURLToPath(new URL("../messages", import.meta.url));

function loadCatalog(locale: string): Catalog {
  return JSON.parse(readFileSync(`${messagesDir}/${locale}.json`, "utf8")) as Catalog;
}

/** `{ Home: { title: "x" } }` → `{ "Home.title": "x" }`. */
function flatten(catalog: Catalog, prefix = ""): Record<string, string> {
  return Object.entries(catalog).reduce<Record<string, string>>((flat, [key, value]) => {
    const path = prefix ? `${prefix}.${key}` : key;
    return typeof value === "string"
      ? { ...flat, [path]: value }
      : { ...flat, ...flatten(value, path) };
  }, {});
}

/** Walks the whole AST, including the bodies of `plural`/`select` branches. */
function walk(elements: MessageFormatElement[], visit: (el: MessageFormatElement) => void): void {
  for (const element of elements) {
    visit(element);
    if (isPluralElement(element) || isSelectElement(element)) {
      for (const option of Object.values(element.options)) {
        walk(option.value, visit);
      }
    }
  }
}

/** Every argument name referenced by a message, e.g. `["count", "name"]`. */
function icuArguments(message: string): string[] {
  const names: string[] = [];
  walk(parse(message), (element) => {
    if (element.type !== TYPE.literal && element.type !== TYPE.pound) {
      names.push(element.value);
    }
  });
  return [...new Set(names)].sort();
}

/** Plural/selectordinal categories declared in a message — `one`, `few`, `other`, … */
function pluralCategories(message: string): string[] {
  const categories: string[] = [];
  walk(parse(message), (element) => {
    if (isPluralElement(element)) {
      // Explicit `=0` / `=1` matches are exact values, not CLDR categories.
      categories.push(...Object.keys(element.options).filter((key) => !key.startsWith("=")));
    }
  });
  return categories;
}

const catalogs = Object.fromEntries(
  routing.locales.map((locale) => [locale, flatten(loadCatalog(locale))]),
) as Record<string, Record<string, string>>;

const reference = catalogs[routing.defaultLocale];
const otherLocales = routing.locales.filter((locale) => locale !== routing.defaultLocale);

describe("message catalogs", () => {
  it("ships a catalog for every configured locale", () => {
    for (const locale of routing.locales) {
      expect(Object.keys(catalogs[locale]).length, `${locale}.json is empty`).toBeGreaterThan(0);
    }
  });

  describe.each(otherLocales)(`%s vs. the ${routing.defaultLocale} reference`, (locale) => {
    it("has no missing keys", () => {
      const missing = Object.keys(reference).filter((key) => !(key in catalogs[locale]));
      expect(missing).toEqual([]);
    });

    it("has no extra keys", () => {
      const extra = Object.keys(catalogs[locale]).filter((key) => !(key in reference));
      expect(extra).toEqual([]);
    });

    it("uses the same ICU arguments for every key", () => {
      const mismatched = Object.entries(reference)
        .filter(([key, message]) => {
          const translation = catalogs[locale][key];
          return (
            translation !== undefined &&
            icuArguments(message).join() !== icuArguments(translation).join()
          );
        })
        .map(([key]) => key);

      expect(mismatched).toEqual([]);
    });
  });

  describe.each(routing.locales)("%s", (locale) => {
    it("has no blank values", () => {
      const blank = Object.entries(catalogs[locale])
        .filter(([, message]) => message.trim() === "")
        .map(([key]) => key);

      expect(blank).toEqual([]);
    });

    it("parses as valid ICU", () => {
      for (const [key, message] of Object.entries(catalogs[locale])) {
        expect(() => parse(message), `${locale}.json → ${key}`).not.toThrow();
      }
    });

    it("only declares plural categories that exist in the locale", () => {
      const valid = new Set(new Intl.PluralRules(locale).resolvedOptions().pluralCategories);
      const invalid = Object.entries(catalogs[locale])
        .flatMap(([key, message]) =>
          pluralCategories(message)
            .filter((category) => !valid.has(category as Intl.LDMLPluralRule))
            .map((category) => `${key}: ${category}`),
        )
        .sort();

      expect(invalid).toEqual([]);
    });
  });
});
