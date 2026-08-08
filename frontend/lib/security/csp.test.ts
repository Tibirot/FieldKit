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
    const connect = production({ keycloak: null })["connect-src"];

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
