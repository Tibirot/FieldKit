import { describe, expect, it } from "vitest";

import { routing } from "@/i18n/routing";

import { BRAND, buildManifest } from "./manifest";

/**
 * The manifest is what makes FieldKit installable (OFF-10). It is generated per locale, so the
 * things worth pinning are the ones that differ per locale — and the two that must *not*.
 */

const strings = { name: "FieldKit", shortName: "FieldKit", description: "Sales force automation." };

describe("web-app manifest", () => {
  describe.each(routing.locales)("%s", (locale) => {
    const manifest = buildManifest(locale, strings);

    it("launches into its own locale", () => {
      expect(manifest.start_url).toBe(`/${locale}`);
      expect(manifest.lang).toBe(locale);
    });

    it("scopes to the whole origin so the locale switcher stays in the installed app", () => {
      expect(manifest.scope).toBe("/");
    });

    it("is installable — the fields a browser requires before it offers 'Install'", () => {
      expect(manifest.name).toBe(strings.name);
      expect(manifest.short_name).toBe(strings.shortName);
      expect(manifest.display).toBe("standalone");
      expect(manifest.icons?.length).toBeGreaterThan(0);
    });

    it("ships a maskable icon distinct from the rounded one", () => {
      const maskable = manifest.icons?.filter((icon) => icon.purpose === "maskable") ?? [];
      const any = manifest.icons?.filter((icon) => icon.purpose === "any") ?? [];

      expect(maskable).toHaveLength(1);
      expect(any.map((icon) => icon.src)).not.toContain(maskable[0].src);
    });

    it("uses hex colours — the manifest spec does not accept the oklch() design tokens", () => {
      expect(manifest.theme_color).toMatch(/^#[0-9A-F]{6}$/);
      expect(manifest.background_color).toMatch(/^#[0-9A-F]{6}$/);
    });
  });

  it("keeps one app identity across locales, so a second install re-points the first", () => {
    const ids = new Set(routing.locales.map((locale) => buildManifest(locale, strings).id));

    expect(ids).toEqual(new Set(["/"]));
  });

  it("declares icon sizes that match the files it points at", () => {
    for (const icon of buildManifest(routing.defaultLocale, strings).icons ?? []) {
      expect(icon.src, `${icon.src} should be named for its declared size`).toContain(
        icon.sizes?.split("x")[0],
      );
    }
  });

  it("exposes brand colours as sRGB hex", () => {
    for (const [token, value] of Object.entries(BRAND)) {
      expect(value, token).toMatch(/^#[0-9A-F]{6}$/);
    }
  });
});
