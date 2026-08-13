import createMiddleware from "next-intl/middleware";
import { NextRequest, NextResponse } from "next/server";

import { routing } from "./i18n/routing";
import { upstreamUrl } from "./lib/api/upstream";
import { contentSecurityPolicy, newNonce, originOf } from "./lib/security/csp";

/**
 * Negotiates the locale (URL prefix → `NEXT_LOCALE` cookie → `Accept-Language` → default) and
 * redirects unprefixed paths to a prefixed one.
 *
 * `proxy.ts` is Next 16's replacement for the deprecated `middleware.ts` convention — same
 * contract, new file name.
 */
const negotiateLocale = createMiddleware(routing);

/**
 * Aspire's service-discovery keys for Keycloak, mirroring `lib/auth/settings.ts`.
 *
 * Read here as well as there because the two need it for different reasons and at different times:
 * that one hands the browser an address to talk to, this one names the address the browser is
 * *allowed* to talk to. Keeping them in step matters — a CSP that omits Keycloak breaks sign-in with
 * a console error and no message on screen.
 */
const KEYCLOAK_KEYS = ["services__keycloak__https__0", "services__keycloak__http__0"];

function keycloakOrigin(): string | null {
  // Same precedence as `lib/auth/settings.ts`, and for the same reason: this names the origin the
  // **browser** is allowed to reach, so an explicit public address beats service discovery — which
  // in Azure Container Apps returns an internal FQDN no browser can resolve. Getting this wrong
  // puts the wrong origin in the CSP, and a CSP that omits Keycloak breaks sign-in with a console
  // error and nothing on screen.
  return originOf(process.env.KEYCLOAK_URL ?? KEYCLOAK_KEYS.map((key) => process.env[key]).find(Boolean));
}

/**
 * Where the browser is allowed to `PUT` a photograph (`OFF-08`, `B5`) — W11 slice 12c.
 *
 * <b>Found in a browser, and it made the feature not work at all.</b> Uploads go straight to object
 * storage on a presigned URL — that is the whole of `B5`'s second transport — and object storage is
 * a *different origin* from this app. `connect-src` did not name it, so every upload was refused by
 * the browser before a byte left the device: the presign succeeded, the `PUT` never happened, and
 * the uploader's own retry made it look like a flaky network forever.
 *
 * <b>Set explicitly rather than derived from a connection string.</b> The server gets
 * `ConnectionStrings__photos` because it needs a credential; the browser needs only an origin, and
 * handing the front end a string containing an account key so it can parse one substring out would
 * be putting a secret where a secret has no business being.
 */
function photoStorageOrigin(): string | null {
  return originOf(process.env.PHOTO_STORAGE_URL);
}

/**
 * Locale negotiation, plus the security headers every document response carries.
 *
 * The CSP is set on the **request** headers as well as the response. That is not belt-and-braces:
 * it is how Next.js learns the nonce. It reads the policy off the incoming request, extracts the
 * nonce, and stamps it onto the script tags it renders itself — without this, its own bootstrap is
 * refused by the very policy this sets and the page renders blank.
 */
export default function proxy(request: NextRequest) {
  // `/api/*` is a different job from everything below: no locale, no nonce, no CSP — the response
  // is JSON for a `fetch`, not a document for a browser to render. It leaves here before any of
  // that runs. See lib/api/upstream.ts for why this is not a `rewrites()` entry.
  if (request.nextUrl.pathname.startsWith("/api/")) {
    const upstream = upstreamUrl(request.nextUrl.pathname, request.nextUrl.search);

    // No API configured. 503 rather than falling through to the app, which would answer a data
    // request with an HTML 404 page — a shape the client cannot parse and would report as a JSON
    // error, sending whoever debugs it looking for a bug in the response body.
    return upstream
      ? NextResponse.rewrite(upstream)
      : new NextResponse(null, { status: 503, statusText: "API not configured" });
  }

  const nonce = newNonce();
  const policy = contentSecurityPolicy({
    nonce,
    keycloak: keycloakOrigin(),
    photoStorage: photoStorageOrigin(),
    development: process.env.NODE_ENV !== "production",
  });

  const headers = new Headers(request.headers);
  headers.set("content-security-policy", policy);
  headers.set("x-nonce", nonce);

  const response = negotiateLocale(new NextRequest(request, { headers }));

  response.headers.set("content-security-policy", policy);

  // Not part of the CSP, and cheap. `nosniff` stops a response being executed as a type it did not
  // declare; the referrer policy keeps a full URL — which for this app can name a territory or an
  // outlet — from leaving with an outbound request.
  response.headers.set("x-content-type-options", "nosniff");
  response.headers.set("referrer-policy", "strict-origin-when-cross-origin");

  return response;
}

export const config = {
  matcher: [
    // The API, which this now forwards rather than merely declining to touch.
    "/api/:path*",
    // Everything except the API, Next internals, and files with an extension (static assets, the
    // PWA manifest, the service worker) — those must never be locale-redirected.
    "/((?!api|_next|_vercel|.*\\..*).*)",
  ],
};
