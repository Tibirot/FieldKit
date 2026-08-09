import type { NextConfig } from "next";
import createNextIntlPlugin from "next-intl/plugin";

const nextConfig: NextConfig = {
  // Self-contained server output for containerised deploy (ADR-0004 / ADR-0011).
  output: "standalone",
  reactCompiler: true,

  /*
   * The API is served same-origin under `/api/`, proxied rather than called cross-origin.
   *
   * Two things already assume this. The service worker refuses to cache anything under `/api/`
   * by pathname (`sw/index.js`), which only means something if the API *is* under `/api/`. And a
   * cross-origin API would put a CORS preflight in front of every call a rep makes on a slow link,
   * to buy nothing — the browser and the API are one deployment.
   *
   * **The forwarding itself is in `proxy.ts`, not in a `rewrites()` entry here, and that is not a
   * style choice.** `rewrites()` is evaluated by `next build` and frozen into the routes manifest,
   * so a container image built where the API does not yet exist ships with no rewrite at all —
   * which is every image built by CI. `lib/api/upstream.ts` has the evidence.
   */
};

// Resolves `i18n/request.ts` and makes the message catalogs available to the App Router (ADR-0010).
const withNextIntl = createNextIntlPlugin();

export default withNextIntl(nextConfig);
