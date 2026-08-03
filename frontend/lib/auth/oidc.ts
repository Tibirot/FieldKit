import { UserManager, WebStorageStateStore } from "oidc-client-ts";

import { realmForWorkspace } from "./workspace";

/**
 * Browser-side OIDC against a tenant's Keycloak realm — authorization code + PKCE (ADR-0008).
 *
 * The tokens live in the browser rather than behind a server session on purpose: the field app has
 * to tolerate going offline mid-shift and refresh on reconnect ([IAM §7]), which a server-side
 * cookie session cannot do once the server is unreachable. That is a deliberate trade — an XSS on
 * this origin reaches the tokens — and it is why the app ships a strict CSP and why no third-party
 * script is loaded into it.
 *
 * [IAM §7]: docs/product/10-identity-and-access.md#7-offline-behavior
 */

/** Everything the browser needs to talk to Keycloak, resolved server-side and passed down. */
export type OidcSettings = {
  /** Keycloak's base address, as the *browser* can reach it. */
  authority: string;
  /** The public client defined in every tenant realm. */
  clientId: string;
};

/**
 * The issuer for a realm.
 *
 * Must be the address the browser is redirected to, not an internal one: Keycloak derives a token's
 * `iss` from the host it was called on, and the API resolves that issuer against its tenant
 * registry. Reach the same Keycloak by a second address and every token it mints is rejected.
 */
export function authorityFor(settings: OidcSettings, workspace: string): string {
  return `${settings.authority.replace(/\/$/, "")}/realms/${realmForWorkspace(workspace)}`;
}

/**
 * Where Keycloak sends the browser back. Locale-prefixed because every route in this app is
 * (`localePrefix: "always"`), so an unprefixed callback would be redirected by the locale
 * negotiator mid-flow.
 */
export function redirectUriFor(origin: string, locale: string): string {
  return `${origin}/${locale}/auth/callback`;
}

/**
 * A `UserManager` for one workspace.
 *
 * Not cached: it is cheap to construct, and a cache keyed by workspace is a way to keep talking to
 * the previous tenant's realm after someone signs out and into another.
 */
export function createUserManager(
  settings: OidcSettings,
  workspace: string,
  origin: string,
  locale: string,
): UserManager {
  return new UserManager({
    authority: authorityFor(settings, workspace),
    client_id: settings.clientId,
    redirect_uri: redirectUriFor(origin, locale),
    post_logout_redirect_uri: `${origin}/${locale}`,
    response_type: "code",
    scope: "openid profile email",

    // localStorage, not the sessionStorage default. A rep who reopens the app after the phone
    // killed the tab is not re-authenticating in a stockroom with no signal; the session has to
    // outlive the tab for the offline story to hold at all.
    userStore: new WebStorageStateStore({ store: window.localStorage }),

    // The in-flight request (PKCE verifier, state, nonce) is the opposite case — it is scoped to
    // one redirect and worthless afterwards, so it goes in sessionStorage and dies with the tab.
    stateStore: new WebStorageStateStore({ store: window.sessionStorage }),

    // Refresh ahead of expiry using the refresh token rather than a hidden iframe: iframes need a
    // live Keycloak session cookie and third-party cookie rules to cooperate, and neither is a bet
    // worth making on a phone. Renewal still needs the network — surviving a *long* offline stretch
    // needs `offline_access`, which is realm configuration and lands with provisioning (`IAM-10`).
    automaticSilentRenew: true,

    // The `permissions` claim is on the access token, not the profile endpoint, and calling
    // userinfo would be a second round trip to learn nothing new.
    loadUserInfo: false,
  });
}
