import "server-only";

import type { OidcSettings } from "./oidc";

/**
 * Reads the Keycloak address from the environment, server-side, so it can be handed to the client
 * as props.
 *
 * Not `NEXT_PUBLIC_*`: those are inlined at build time, and Aspire assigns Keycloak's port per run.
 * A baked-in port is wrong the moment the AppHost restarts, and — worse — wrong in a way that
 * produces valid-looking tokens the API rejects, because the issuer no longer matches. Reading it
 * per request and passing it down keeps one source of truth.
 */

/** Aspire's service-discovery keys for the `keycloak` resource, most-preferred first. */
const ASPIRE_KEYS = ["services__keycloak__https__0", "services__keycloak__http__0"];

/** The client id every tenant realm defines (see `FieldKit.AppHost/realms/README.md`). */
const CLIENT_ID = "fieldkit-web";

/**
 * The OIDC settings, or `null` when Keycloak is not configured.
 *
 * Null rather than throwing: `next build` runs without the AppHost, and a hard failure there would
 * make the app unbuildable outside Aspire. The sign-in screen renders an explanatory state instead,
 * which is also what a misconfigured deployment should show.
 */
export function readOidcSettings(): OidcSettings | null {
  const authority =
    ASPIRE_KEYS.map((key) => process.env[key]).find(Boolean) ?? process.env.KEYCLOAK_URL;

  return authority ? { authority, clientId: CLIENT_ID } : null;
}
