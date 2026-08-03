import type { NextConfig } from "next";
import createNextIntlPlugin from "next-intl/plugin";

/**
 * The API's address, from Aspire's `WithReference(server)`. Assigned per run, so it is read at
 * startup rather than hard-coded.
 */
const apiOrigin =
  process.env.services__server__https__0 ??
  process.env.services__server__http__0 ??
  process.env.API_URL;

const nextConfig: NextConfig = {
  // Self-contained server output for containerised deploy (ADR-0004 / ADR-0011).
  output: "standalone",
  reactCompiler: true,

  /**
   * The API is served same-origin under `/api/`, proxied rather than called cross-origin.
   *
   * Two things already assume this. The service worker refuses to cache anything under `/api/`
   * by pathname (`sw/index.js`), which only means something if the API *is* under `/api/`. And a
   * cross-origin API would put a CORS preflight in front of every call a rep makes on a slow link,
   * to buy nothing — the browser and the API are one deployment.
   */
  async rewrites() {
    return apiOrigin
      ? [{ source: "/api/:path*", destination: `${apiOrigin.replace(/\/$/, "")}/api/:path*` }]
      : [];
  },
};

// Resolves `i18n/request.ts` and makes the message catalogs available to the App Router (ADR-0010).
const withNextIntl = createNextIntlPlugin();

export default withNextIntl(nextConfig);
