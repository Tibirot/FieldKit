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

/**
 * Aspire's service-discovery keys for the `keycloak` resource, most-preferred first.
 *
 * **Less preferred than `KEYCLOAK_URL`**, which is the opposite of how it reads and is the point.
 * Service discovery answers "what address do *containers* use to reach Keycloak", and in Azure
 * Container Apps that is an internal FQDN — `keycloak.internal.<env>.azurecontainerapps.io`. This
 * value is handed to the **browser**, which cannot resolve it. The deployed app failed exactly
 * there:
 *
 *     Access to fetch at 'https://keycloak.internal.…/realms/fieldkit-dev/.well-known/openid-configuration'
 *     from origin 'https://webfrontend.…' has been blocked by CORS policy
 *
 * Sign-in still worked, because the *first* redirect is a navigation rather than a fetch. What
 * broke was every silent renewal after it — so a session died at the first token expiry and the app
 * reported it as "Your session has expired", about five minutes after a successful login.
 *
 * The header above already warned about a Keycloak address that is "wrong in a way that produces
 * valid-looking tokens the API rejects". This is that hazard, reached from the other side.
 */
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
  // Explicit first, discovered second. `KEYCLOAK_URL` is set only where the browser-facing address
  // differs from the service-facing one, which is every deployment and no development run.
  const authority =
    process.env.KEYCLOAK_URL ?? ASPIRE_KEYS.map((key) => process.env[key]).find(Boolean);

  return authority ? { authority, clientId: CLIENT_ID } : null;
}
