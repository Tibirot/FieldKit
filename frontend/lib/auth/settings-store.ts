import type { OidcSettings } from "./oidc";

/**
 * The last-known Keycloak address, kept on the device.
 *
 * Two constraints meet here and neither is negotiable. The address must be read at **runtime**,
 * because Aspire assigns Keycloak's port per run and a build-time value produces tokens whose issuer
 * the API refuses. And restoring a session must work **offline**, because a rep opening the app in a
 * stockroom is not going to be told to sign in again — which rules out fetching it when needed.
 *
 * So the two pages that genuinely need a live Keycloak — sign-in and the callback — are rendered
 * dynamically and hand the current address down. This is where they leave it for everything else:
 * silent renew, and reading back the stored session on the next cold start.
 */
const STORAGE_KEY = "fieldkit.oidc";

export function rememberSettings(settings: OidcSettings): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(settings));
  } catch {
    // Signing in still works — this session just cannot be restored after a reload.
  }
}

/**
 * The remembered settings, or `null` if there are none or they are unusable.
 *
 * Validated on the way out rather than trusted: this is attacker-writable storage feeding the
 * `authority` of an OIDC flow, and a value that is not an absolute `http(s)` URL has no business
 * becoming one. A bad entry means "sign in again", which is recoverable; a redirect to somewhere
 * else is not.
 */
export function readSettings(): OidcSettings | null {
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY);
    if (!stored) return null;

    const parsed: unknown = JSON.parse(stored);

    if (
      typeof parsed !== "object" ||
      parsed === null ||
      typeof (parsed as OidcSettings).authority !== "string" ||
      typeof (parsed as OidcSettings).clientId !== "string"
    ) {
      return null;
    }

    const settings = parsed as OidcSettings;
    const protocol = URL.parse(settings.authority)?.protocol;

    return protocol === "https:" || protocol === "http:" ? settings : null;
  } catch {
    return null;
  }
}

export function forgetSettings(): void {
  try {
    window.localStorage.removeItem(STORAGE_KEY);
  } catch {
    // The next sign-in overwrites it.
  }
}
