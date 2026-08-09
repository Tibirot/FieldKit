import { describe, expect, it } from "vitest";

import { apiOrigin, upstreamUrl } from "./upstream";

/**
 * The regression these guard is one no unit test could have caught before the container existed:
 * the mapping used to live in `next.config.ts`, where it was evaluated once by `next build` and
 * could not be called from a test at all. Moving it into a function is most of the fix.
 */
describe("apiOrigin", () => {
  it("prefers the https service key", () => {
    const origin = apiOrigin({
      services__server__https__0: "https://api.example:8443",
      services__server__http__0: "http://api.example:8080",
      API_URL: "http://fallback.example",
    });

    expect(origin).toBe("https://api.example:8443");
  });

  it("falls back to http, then to API_URL", () => {
    expect(
      apiOrigin({ services__server__http__0: "http://api.example:8080", API_URL: "http://x.example" }),
    ).toBe("http://api.example:8080");

    expect(apiOrigin({ API_URL: "http://x.example" })).toBe("http://x.example");
  });

  it("is null when nothing names an API", () => {
    // `next build` runs in exactly this state and must not fail.
    expect(apiOrigin({})).toBeNull();
  });

  it("is null rather than throwing on a value that is not a URL", () => {
    expect(apiOrigin({ API_URL: "not a url" })).toBeNull();
  });

  it("keeps only the origin, so a trailing slash cannot double up in the path", () => {
    expect(apiOrigin({ API_URL: "http://api.example:8080/" })).toBe("http://api.example:8080");
  });
});

describe("upstreamUrl", () => {
  const env = { services__server__https__0: "https://api.example:8443" };

  it("swaps the host and keeps the path, /api prefix included", () => {
    // The server mounts its routes under /api too, so stripping the prefix here would 404 upstream.
    expect(upstreamUrl("/api/outlets/channels", "", env).href).toBe(
      "https://api.example:8443/api/outlets/channels",
    );
  });

  it("carries the query string across", () => {
    expect(upstreamUrl("/api/outlets", "?page=2&ids=a,b", env).href).toBe(
      "https://api.example:8443/api/outlets?page=2&ids=a,b",
    );
  });

  it("is null when no API is configured", () => {
    expect(upstreamUrl("/api/outlets", "", {})).toBeNull();
  });
});
