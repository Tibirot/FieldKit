/**
 * The Content-Security-Policy this app serves, and why each directive is what it is.
 *
 * **This exists because a comment claimed it already did.** `lib/auth/oidc.ts` justified keeping
 * tokens in the browser partly on the grounds that "the app ships a strict CSP" — and no CSP was
 * ever sent. That is the worst shape a security control can be in: the reasoning that depends on it
 * had already been written down and accepted.
 *
 * The tokens are the thing being protected. Authorization code + PKCE puts an access token in the
 * browser (ADR-0008) so the field app can refresh after going offline mid-shift, which a server-side
 * cookie session cannot do once the server is unreachable. The trade is explicit: an XSS on this
 * origin reaches those tokens. A CSP is what makes injected script hard to execute in the first
 * place, so it is the control that trade was resting on.
 *
 * A pure function, and separately tested, because a policy assembled inline in middleware is one
 * nobody can assert about — and the failure mode of a CSP is silent in exactly the wrong direction:
 * a directive that is too permissive protects nothing and looks identical to one that works.
 */

/** Origins the browser must reach that are not this one. */
export type CspOrigins = {
  /**
   * Keycloak, as the *browser* reaches it — scheme and host only.
   *
   * Needed because the OIDC client talks to it directly: discovery document, JWKS, token endpoint.
   * Null when Keycloak is not configured, which is the same state `readOidcSettings` reports and is
   * normal during `next build`.
   */
  keycloak: string | null;
};

export type CspOptions = CspOrigins & {
  /** The per-request nonce, base64. */
  nonce: string;
  /** Development relaxes two things the dev server cannot work without. See below. */
  development: boolean;
};

/**
 * Just the origin of a URL, or null if it is not one.
 *
 * Keycloak's address arrives from Aspire as a full base URL and may carry a path; a CSP source
 * expression is scheme + host + port, and a trailing path silently narrows the match.
 */
export function originOf(url: string | null | undefined): string | null {
  if (!url) return null;

  try {
    return new URL(url).origin;
  } catch {
    return null;
  }
}

/**
 * Builds the policy.
 *
 * @returns a `Content-Security-Policy` header value.
 */
export function contentSecurityPolicy({ nonce, keycloak, development }: CspOptions): string {
  const directives: Record<string, string[]> = {
    // Everything not named below falls back to this one, so an unlisted fetch type is refused
    // rather than allowed — the whole point of naming a default.
    "default-src": ["'self'"],

    /**
     * `'strict-dynamic'` with a per-request nonce, which is the only shape of script-src worth
     * having here.
     *
     * Next.js serves its own inline bootstrap and its hydration payload as inline `<script>` tags,
     * so the alternatives are a nonce or `'unsafe-inline'` — and `'unsafe-inline'` on this origin
     * would allow exactly the injected script the tokens need protecting from, which would make the
     * claim in oidc.ts false a second time.
     *
     * `'strict-dynamic'` lets the nonced bootstrap load the chunk files it needs without every
     * chunk carrying a nonce of its own. A browser that honours it ignores `'self'` here; one that
     * does not falls back to `'self'`, which is why `'self'` is still listed.
     */
    "script-src": ["'self'", `'nonce-${nonce}'`, "'strict-dynamic'"],

    /**
     * `'unsafe-inline'` for styles, deliberately and with a smaller blast radius than it sounds.
     *
     * Next injects critical CSS inline and does not nonce it, so the choice is this or a broken
     * stylesheet. Injected *style* can deface a page and can be used to exfiltrate some form state
     * through selectors; it cannot execute. Given `script-src` holds the line, this is the trade
     * every Next app makes, and naming it here is better than a reader assuming it was missed.
     */
    "style-src": ["'self'", "'unsafe-inline'"],

    // `data:` for the inline SVG icons the design system emits; `blob:` for anything generated in
    // the browser (a photo taken on a visit is Phase 3, and will land here rather than widen later).
    "img-src": ["'self'", "data:", "blob:"],
    "font-src": ["'self'"],

    // The API is same-origin — it is proxied under `/api/` rather than called cross-origin
    // (next.config.ts) — so `'self'` covers every call the app makes except Keycloak's.
    "connect-src": ["'self'", ...(keycloak ? [keycloak] : [])],

    // The service worker, which is what makes this a PWA at all.
    "worker-src": ["'self'"],
    "manifest-src": ["'self'"],

    // Nothing here is meant to be embedded, and nothing here embeds anything.
    "frame-ancestors": ["'none'"],
    "frame-src": ["'none'"],
    "object-src": ["'none'"],

    // A `<base>` tag injected into the document would silently re-point every relative URL on the
    // page, including the ones that carry a token.
    "base-uri": ["'self'"],
    "form-action": ["'self'"],
  };

  if (development) {
    // The dev server compiles with `eval` and talks to the browser over a websocket. Both are
    // refused by the policy above, and neither is present in a production build — which is why this
    // is a branch rather than a permanent widening.
    directives["script-src"] = [...directives["script-src"], "'unsafe-eval'"];
    directives["connect-src"] = [...directives["connect-src"], "ws:", "wss:"];
  }

  return Object.entries(directives)
    .map(([directive, sources]) => `${directive} ${sources.join(" ")}`)
    .join("; ");
}

/**
 * A fresh nonce, 128 bits, base64.
 *
 * Per request and never reused: a nonce a page reuses is a nonce an attacker can read off that page
 * and put on a script of their own, which is the same as having no nonce at all.
 */
export function newNonce(): string {
  return btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(16))));
}
