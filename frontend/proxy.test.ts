import { NextRequest } from "next/server";
import { afterEach, describe, expect, it, vi } from "vitest";

import proxy from "./proxy";

/**
 * The routing decision, as opposed to the URL arithmetic (`lib/api/upstream.test.ts`).
 *
 * What matters here is that `/api/*` leaves before the document pipeline: no locale redirect, no
 * CSP, no nonce. A data request that picks up a locale prefix becomes `/en/api/...` upstream and
 * 404s; one that picks up a CSP is merely wasteful, but both mean the request went through code
 * written for pages.
 */
function request(path: string) {
  return new NextRequest(new URL(path, "http://localhost:3000"));
}

afterEach(() => {
  vi.unstubAllEnvs();
});

describe("proxy", () => {
  it("forwards /api to the configured API and does not redirect it to a locale", () => {
    vi.stubEnv("services__server__https__0", "https://api.example:8443");

    const response = proxy(request("/api/outlets/channels?page=2"));

    expect(response.status).toBe(200);
    expect(response.headers.get("x-middleware-rewrite")).toBe(
      "https://api.example:8443/api/outlets/channels?page=2",
    );
    expect(response.headers.get("location")).toBeNull();
    expect(response.headers.get("content-security-policy")).toBeNull();
  });

  it("answers 503 rather than an HTML 404 when no API is configured", () => {
    // The client parses this response as JSON. Falling through to the app would hand it the
    // rendered 404 page and the failure would be reported as a parse error.
    const response = proxy(request("/api/outlets/channels"));

    expect(response.status).toBe(503);
  });

  it("still negotiates locale and sets the CSP for a document request", () => {
    const response = proxy(request("/outlets"));

    expect(response.headers.get("content-security-policy")).toContain("default-src 'self'");
    expect(response.headers.get("x-content-type-options")).toBe("nosniff");
  });

  it("does not treat a page whose path merely starts with 'api' as an API call", () => {
    // `/apiary` starts with "api" but is a page. The guard is on "/api/", not "/api" — with the
    // API configured, so a prefix match would forward it somewhere visible rather than do nothing.
    vi.stubEnv("services__server__https__0", "https://api.example:8443");

    const response = proxy(request("/apiary"));

    expect(response.headers.get("x-middleware-rewrite")).toBeNull();
    // It went down the document path instead: locale-redirected, and carrying the CSP.
    expect(response.headers.get("location")).toContain("/en/apiary");
    expect(response.headers.get("content-security-policy")).toContain("default-src 'self'");
  });
});
