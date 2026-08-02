import createMiddleware from "next-intl/middleware";

import { routing } from "./i18n/routing";

/**
 * Negotiates the locale (URL prefix → `NEXT_LOCALE` cookie → `Accept-Language` → default) and
 * redirects unprefixed paths to a prefixed one.
 *
 * `proxy.ts` is Next 16's replacement for the deprecated `middleware.ts` convention — same
 * contract, new file name.
 */
export default createMiddleware(routing);

export const config = {
  // Everything except API routes, Next internals, and files with an extension (static assets,
  // the PWA manifest, the service worker) — those must never be locale-redirected.
  matcher: "/((?!api|_next|_vercel|.*\\..*).*)",
};
