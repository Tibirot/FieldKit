// @vitest-environment jsdom

import { beforeEach, describe, expect, it } from "vitest";

import {
  applyTheme,
  classFor,
  DEFAULT_THEME,
  isTheme,
  rememberTheme,
  storedTheme,
  THEME_BOOTSTRAP,
  THEME_STORAGE_KEY,
  THEMES,
} from "@/lib/theme/theme";

/** Runs the pre-paint script the way the browser does — against this document, from scratch. */
function bootstrap() {
  document.documentElement.className = "font-sans";
  new Function(THEME_BOOTSTRAP)();

  return document.documentElement.className;
}

/** The same question asked of the module, so the two answers can be compared. */
function runtime(theme: (typeof THEMES)[number]) {
  document.documentElement.className = "font-sans";
  applyTheme(theme, document.documentElement);

  return document.documentElement.className;
}

beforeEach(() => {
  localStorage.clear();
  document.documentElement.className = "font-sans";
});

describe("the theme vocabulary", () => {
  it("defaults to light rather than to the device", () => {
    /*
     * The decision, not an implementation detail. Resolving an unset choice to `system` would leave
     * the app doing what it already did and would make "light by default" false — a laptop set to
     * dark would still open dark. `system` exists as something a person picks, not as the floor.
     */
    expect(DEFAULT_THEME).toBe("light");
    expect(storedTheme()).toBe("light");
  });

  it("spells `system` as the absence of a class", () => {
    // `globals.css` reads no class as "obey prefers-color-scheme", so following the device is the
    // absence of an instruction. There is no `.system` for this to return.
    expect(classFor("system")).toBeNull();
    expect(classFor("light")).toBe("light");
    expect(classFor("dark")).toBe("dark");
  });

  it("never leaves both classes on at once", () => {
    // `.light` and `.dark` together is a document whose palette depends on source order — the one
    // state the cascade contract in globals.css cannot describe.
    for (const theme of THEMES) {
      const classes = runtime(theme).split(" ");

      expect(classes.filter((name) => name === "light" || name === "dark").length).toBeLessThan(2);
    }
  });

  it("keeps whatever else the document was wearing", () => {
    // `<html>` carries the font variables. A theme switch that replaced `className` would drop them
    // and the page would fall back to a system font mid-session.
    expect(runtime("dark")).toContain("font-sans");
  });

  it("refuses a stored value that is not a theme", () => {
    // localStorage is a shared, writable, untyped surface — an older build, another tab, or a hand
    // edit can leave anything in there, and `classList.add("<script>")` is not a thing to attempt.
    localStorage.setItem(THEME_STORAGE_KEY, "midnight");

    expect(storedTheme()).toBe(DEFAULT_THEME);
    expect(isTheme("midnight")).toBe(false);
  });

  it("survives a storage that refuses to answer", () => {
    /*
     * Safari's private mode, a locked-down profile, an iframe with third-party storage blocked —
     * `localStorage` throws on access rather than returning null. A theme is not worth a blank page.
     */
    const real = Object.getOwnPropertyDescriptor(globalThis, "localStorage");

    Object.defineProperty(globalThis, "localStorage", {
      configurable: true,
      get() {
        throw new DOMException("denied", "SecurityError");
      },
    });

    expect(storedTheme()).toBe(DEFAULT_THEME);
    expect(() => rememberTheme("dark")).not.toThrow();

    if (real) Object.defineProperty(globalThis, "localStorage", real);
  });
});

describe("the pre-paint script", () => {
  /*
   * **The parity check, and the reason the duplication is allowed to exist.**
   *
   * The script cannot import the module — it runs inline in the `<head>`, before anything is
   * fetched — so there are two implementations of one rule. That is the shape the pricing vectors
   * exist to police, and it is safe only while something compares them. This is that something.
   */
  it.each(THEMES)("agrees with applyTheme about %s", (theme) => {
    localStorage.setItem(THEME_STORAGE_KEY, theme);

    expect(bootstrap()).toBe(runtime(theme));
  });

  it("agrees about an unset choice too", () => {
    localStorage.clear();

    expect(bootstrap()).toBe(runtime(DEFAULT_THEME));
  });

  it("agrees about a stored value that is not a theme", () => {
    localStorage.setItem(THEME_STORAGE_KEY, "midnight");

    expect(bootstrap()).toBe(runtime(DEFAULT_THEME));
  });

  it("falls back to light rather than throwing where storage is denied", () => {
    // An exception here runs before the body exists, and an uncaught one in the head can stop the
    // document. Light with no memory beats no page.
    const real = Object.getOwnPropertyDescriptor(globalThis, "localStorage");

    Object.defineProperty(globalThis, "localStorage", {
      configurable: true,
      get() {
        throw new DOMException("denied", "SecurityError");
      },
    });

    expect(bootstrap()).toBe(runtime(DEFAULT_THEME));

    if (real) Object.defineProperty(globalThis, "localStorage", real);
  });

  it("stays one statement, so it can go in an attribute", () => {
    // Newlines are stripped on purpose: this is interpolated into a <script> tag, and a stray
    // line break inside a string literal would be a syntax error the browser reports at parse time
    // — before anything on the page can report it anywhere useful.
    expect(THEME_BOOTSTRAP).not.toContain("\n");

    // And the key and the class names come from the constants, so a rename cannot leave it behind.
    expect(THEME_BOOTSTRAP).toContain(THEME_STORAGE_KEY);
  });
});
