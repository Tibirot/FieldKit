import { beforeEach, describe, expect, it, vi } from "vitest";

import { authorityFor, redirectUriFor } from "./oidc";
import { forgetSettings, readSettings, rememberSettings } from "./settings-store";
import {
  forgetWorkspace,
  isValidWorkspace,
  normalizeWorkspace,
  readWorkspace,
  realmForWorkspace,
  rememberWorkspace,
} from "./workspace";

const SETTINGS = { authority: "https://localhost:8443", clientId: "fieldkit-web" };

describe("workspace validation", () => {
  it.each([
    ["fieldkit-dev", true],
    ["fieldkit-dev-b", true],
    ["a1", true],
    ["A", false], // uppercase never reaches here — `normalizeWorkspace` runs first
    ["x", false], // a single character cannot name a realm
    ["-leading", false],
    ["has space", false],
    ["has/slash", false], // would escape the realm segment of the authority URL
    ["has.dot", false],
    ["", false],
  ])("%s → %s", (value, expected) => {
    expect(isValidWorkspace(value)).toBe(expected);
  });

  it("rejects anything long enough to be a pasted URL", () => {
    expect(isValidWorkspace("a".repeat(65))).toBe(false);
  });

  it("normalizes what people actually type", () => {
    expect(normalizeWorkspace("  Veridian  ")).toBe("veridian");
  });
});

describe("authority", () => {
  it("addresses the realm the workspace maps to", () => {
    expect(authorityFor(SETTINGS, "fieldkit-dev")).toBe(
      "https://localhost:8443/realms/fieldkit-dev",
    );
  });

  it("does not double the slash when the configured address has a trailing one", () => {
    expect(authorityFor({ ...SETTINGS, authority: "https://localhost:8443/" }, "x")).toBe(
      "https://localhost:8443/realms/x",
    );
  });

  it("maps workspace to realm through one seam", () => {
    // Identity today. The assertion exists so that changing it is a deliberate act with a failing
    // test attached, rather than a quiet edit that breaks every existing device's remembered value.
    expect(realmForWorkspace("fieldkit-dev")).toBe("fieldkit-dev");
  });
});

describe("redirect uri", () => {
  it("is locale-prefixed, because every route in this app is", () => {
    // An unprefixed callback is redirected by the locale negotiator mid-flow, which drops the
    // authorization code.
    expect(redirectUriFor("http://localhost:3000", "ro")).toBe(
      "http://localhost:3000/ro/auth/callback",
    );
  });
});

/**
 * These tests run under vitest's `node` environment, so `window` is stubbed rather than provided by
 * jsdom. The storage helpers touch exactly one thing — `window.localStorage` — and a fake makes the
 * "storage throws" case, which is the one worth testing, trivial to arrange.
 */
function stubStorage(overrides: Partial<Storage> = {}) {
  const entries = new Map<string, string>();

  const storage: Partial<Storage> = {
    getItem: (key) => entries.get(key) ?? null,
    setItem: (key, value) => void entries.set(key, value),
    removeItem: (key) => void entries.delete(key),
    ...overrides,
  };

  vi.stubGlobal("window", { localStorage: storage });
}

describe("remembering the workspace", () => {
  beforeEach(() => {
    vi.unstubAllGlobals();
    stubStorage();
  });

  it("round-trips", () => {
    rememberWorkspace("fieldkit-dev");
    expect(readWorkspace()).toBe("fieldkit-dev");

    forgetWorkspace();
    expect(readWorkspace()).toBeNull();
  });

  it("ignores a stored value that is no longer valid", () => {
    // Whatever wrote it, it is not going into an authority URL.
    rememberWorkspace("../../evil");
    expect(readWorkspace()).toBeNull();
  });

  it("degrades to asking again when storage throws", () => {
    // Safari private mode and lockdown policies both do this. Signing in must still be possible,
    // and a blank screen is not an acceptable way to say "I could not remember your workspace".
    const denied = () => {
      throw new Error("denied");
    };

    stubStorage({ getItem: denied, setItem: denied, removeItem: denied });

    expect(readWorkspace()).toBeNull();
    expect(() => rememberWorkspace("fieldkit-dev")).not.toThrow();
    expect(() => forgetWorkspace()).not.toThrow();
  });
});

describe("remembering where Keycloak is", () => {
  beforeEach(() => {
    vi.unstubAllGlobals();
    stubStorage();
  });

  it("round-trips, so a session survives a cold start with no network", () => {
    rememberSettings(SETTINGS);
    expect(readSettings()).toEqual(SETTINGS);

    forgetSettings();
    expect(readSettings()).toBeNull();
  });

  it.each([
    ["not json at all", "{{{"],
    ["missing the client id", JSON.stringify({ authority: "https://localhost:8443" })],
    ["an authority that is not a url", JSON.stringify({ authority: "nope", clientId: "x" })],
    // The one that matters: this value feeds the `authority` of an OIDC redirect, so a stored
    // `javascript:` or `data:` URL must not become one.
    ["a non-http scheme", JSON.stringify({ authority: "javascript:alert(1)", clientId: "x" })],
  ])("refuses %s", (_, stored) => {
    window.localStorage.setItem("fieldkit.oidc", stored);
    expect(readSettings()).toBeNull();
  });
});
