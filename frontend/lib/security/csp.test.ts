import { describe, expect, it } from "vitest";

import { contentSecurityPolicy, originOf } from "@/lib/security/csp";

/** The policy as a lookup, so a test can ask about one directive without matching a string. */
function directives(policy: string): Record<string, string[]> {
  return Object.fromEntries(
    policy.split("; ").map((part) => {
      const [name, ...sources] = part.split(" ");
      return [name, sources];
    }),
  );
}

const production = (over: Partial<Parameters<typeof contentSecurityPolicy>[0]> = {}) =>
  directives(
    contentSecurityPolicy({
      nonce: "n0nce",
      keycloak: "https://keycloak.example:8443",
      photoStorage: "https://fieldkit.blob.core.windows.net",
      development: false,
      ...over,
    }),
  );

describe("contentSecurityPolicy", () => {
  it("refuses inline script, which is the whole reason it exists", () => {
    // The tokens live in the browser (ADR-0008), so an XSS on this origin reaches them — that is
    // the trade oidc.ts documents and this policy is what it rests on. `'unsafe-inline'` here would
    // allow exactly the injected script the arrangement is meant to survive.
    expect(production()["script-src"]).not.toContain("'unsafe-inline'");
    expect(production()["script-src"]).not.toContain("'unsafe-eval'");
  });

  it("carries the nonce it was given, and lets it load the app's own chunks", () => {
    const script = production()["script-src"];

    expect(script).toContain("'nonce-n0nce'");

    // Without `'strict-dynamic'` every chunk Next loads would need a nonce of its own; with it, the
    // nonced bootstrap is trusted to load them. `'self'` stays for browsers that ignore it.
    expect(script).toContain("'strict-dynamic'");
    expect(script).toContain("'self'");
  });

  it("lets the browser reach Keycloak, because the OIDC client talks to it directly", () => {
    // Discovery document, JWKS, token endpoint. Omitting this breaks sign-in with a console error
    // and nothing on screen, which is the failure mode worth a test.
    expect(production()["connect-src"]).toContain("https://keycloak.example:8443");
  });

  it("names no Keycloak when there is none, rather than an empty source", () => {
    // `next build` runs without the AppHost and `readOidcSettings` answers null there. A stray
    // empty string in the source list is a parse error for the whole directive.
    //
    // Photo storage is nulled too, so this stays a claim about *Keycloak* being absent rather than a
    // claim about the whole directive — which is what made it fail when the storage origin arrived.
    const connect = production({ keycloak: null, photoStorage: null })["connect-src"];

    expect(connect).toEqual(["'self'"]);
  });

  it("does not need to name the API, because the API is same-origin", () => {
    // Proxied under `/api/` rather than called cross-origin (next.config.ts). If that ever changes,
    // this test is where the CSP finds out.
    expect(production()["connect-src"]).toContain("'self'");
  });

  it("shuts the doors that have nothing to do with this app", () => {
    const policy = production();

    expect(policy["frame-ancestors"]).toEqual(["'none'"]);
    expect(policy["object-src"]).toEqual(["'none'"]);

    // A `<base>` injected into the document re-points every relative URL on the page.
    expect(policy["base-uri"]).toEqual(["'self'"]);
    expect(policy["form-action"]).toEqual(["'self'"]);
  });

  it("allows the service worker, which is what makes this a PWA", () => {
    expect(production()["worker-src"]).toContain("'self'");
  });

  it("relaxes exactly two things in development, and only there", () => {
    // The dev server compiles with `eval` and talks over a websocket. Both must be absent from a
    // production policy, which is the half of this that matters.
    const dev = production({ development: true });

    expect(dev["script-src"]).toContain("'unsafe-eval'");
    expect(dev["connect-src"]).toContain("ws:");

    expect(production()["script-src"]).not.toContain("'unsafe-eval'");
    expect(production()["connect-src"]).not.toContain("ws:");
  });

  it("falls back to default-src for anything not named", () => {
    expect(production()["default-src"]).toEqual(["'self'"]);
  });
});

describe("originOf", () => {
  it("keeps scheme, host and port and drops the rest", () => {
    // Aspire hands over a base URL that may carry a path, and a CSP source is an origin — a
    // trailing path silently narrows what the directive matches.
    expect(originOf("https://keycloak.example:8443/auth/")).toBe("https://keycloak.example:8443");
  });

  it("answers null for nothing and for nonsense", () => {
    expect(originOf(undefined)).toBeNull();
    expect(originOf("")).toBeNull();
    expect(originOf("not a url")).toBeNull();
  });
});

describe("the photo upload's origin", () => {
  /*
   * <b>Regression, found in a browser and not by any suite.</b> `B5` sends photographs straight to
   * object storage on a presigned URL — that is the whole point of the second transport — and object
   * storage is not this app's origin. `connect-src` did not name it, so the browser refused every
   * `PUT` before a byte left the device: the presign succeeded, the upload never happened, and the
   * uploader's retry made it look like a bad network forever.
   *
   * Neither suite could have caught it. The device tests mock `fetch`; the server tests upload from
   * .NET, where there is no CSP at all.
   */
  it("is allowed to be connected to", () => {
    expect(production()["connect-src"]).toContain("https://fieldkit.blob.core.windows.net");
  });

  it("is absent when no storage is configured, rather than widening to everything", () => {
    // A deployment without photo storage — and every `next build`. The failure mode of a CSP is
    // silent in the permissive direction, so "unset" must narrow rather than open.
    const connect = production({ photoStorage: null })["connect-src"];

    expect(connect).toEqual(["'self'", "https://keycloak.example:8443"]);
  });

  it("does not let object storage do anything but be connected to", () => {
    /*
     * A storage origin in `script-src` would let an attacker who can write a blob — which is what a
     * presigned URL grants, narrowly — serve script to this origin. The upload needs `connect-src`
     * and nothing else.
     */
    const policy = production();

    expect(policy["script-src"]).not.toContain("https://fieldkit.blob.core.windows.net");
    expect(policy["default-src"]).not.toContain("https://fieldkit.blob.core.windows.net");
    expect(policy["frame-src"]).toEqual(["'none'"]);
  });
});

describe("the storage emulator's two names", () => {
  const development = (photoStorage: string | null) =>
    directives(
      contentSecurityPolicy({
        nonce: "n0nce",
        keycloak: "https://keycloak.example:8443",
        photoStorage,
        development: true,
      }),
    );

  it("allows both spellings of loopback in development", () => {
    /*
     * <b>The second half of the same bug, and it survived the first fix.</b> Aspire renders the
     * emulator's endpoint as `localhost`; the Azure SDK signs the SAS for `127.0.0.1`, because that
     * is what the emulator's connection string says. A CSP source matches the host **as written**, so
     * naming one and being sent the other blocks every upload — which is what the browser did after
     * the first attempt, with the port matching and nothing else.
     */
    const connect = development("http://localhost:10000")["connect-src"];

    expect(connect).toContain("http://localhost:10000");
    expect(connect).toContain("http://127.0.0.1:10000");
  });

  it("works whichever way round the configured origin is spelled", () => {
    const connect = development("http://127.0.0.1:10000")["connect-src"];

    expect(connect).toContain("http://127.0.0.1:10000");
    expect(connect).toContain("http://localhost:10000");
  });

  it("does not widen a real storage account, in development or out of it", () => {
    // The sibling only exists for loopback. A deployed account has one name and both halves use it —
    // inventing a second host for `fieldkit.blob.core.windows.net` would be naming an origin nobody
    // serves, which is how a policy quietly stops meaning anything.
    const connect = development("https://fieldkit.blob.core.windows.net")["connect-src"];

    expect(connect.filter((source) => source.includes("blob.core.windows.net"))).toEqual([
      "https://fieldkit.blob.core.windows.net",
    ]);
  });

  it("adds nothing in production, where the emulator does not exist", () => {
    expect(production({ photoStorage: "http://localhost:10000" })["connect-src"]).not.toContain(
      "http://127.0.0.1:10000",
    );
  });
});
