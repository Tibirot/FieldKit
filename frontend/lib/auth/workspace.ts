/**
 * Which tenant a user is signing in to (`IAM-01`, ADR-0008).
 *
 * Realm-per-tenant means the app cannot start an OIDC flow until it knows *which realm* — there is
 * no shared login page to send everyone to. Something has to supply that before the first redirect,
 * and the options are a subdomain (needs wildcard DNS and per-tenant redirect URIs), an email-domain
 * lookup (needs a public endpoint that confirms whether a tenant exists), or asking. This asks, once,
 * and remembers.
 *
 * Everything realm-shaped lives here so the day a convention arrives with provisioning (`IAM-10`),
 * one function changes rather than the login flow.
 */

/** Where the last-used workspace is remembered, so signing in is a one-time question per device. */
const STORAGE_KEY = "fieldkit.workspace";

/**
 * Lowercase letters, digits and hyphens — the same shape a Keycloak realm name takes, because today
 * it *is* the realm name. Bounded at 64 to keep a pasted essay out of a URL.
 */
const WORKSPACE_PATTERN = /^[a-z0-9][a-z0-9-]{1,63}$/;

/** Trims and lowercases what someone typed. Users type `Veridian `; realms are not capitalised. */
export function normalizeWorkspace(raw: string): string {
  return raw.trim().toLowerCase();
}

export function isValidWorkspace(workspace: string): boolean {
  return WORKSPACE_PATTERN.test(workspace);
}

/**
 * The Keycloak realm a workspace maps to.
 *
 * Identity today: the dev realms are literally named `fieldkit-dev` and `fieldkit-dev-b`, and
 * inventing a prefix now would be inventing a convention that tenant provisioning has not chosen
 * yet. When it does, this is the seam — and the API is the authority regardless, since a realm no
 * tenant row claims is refused at token validation whatever the client believes.
 */
export function realmForWorkspace(workspace: string): string {
  return workspace;
}

/**
 * The remembered workspace, or `null`.
 *
 * Returns `null` rather than throwing when storage is unavailable — Safari private mode and
 * lockdown policies make `localStorage` access throw, and "cannot remember your workspace" should
 * degrade to asking again, not to a blank screen.
 */
export function readWorkspace(): string | null {
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY);
    return stored && isValidWorkspace(stored) ? stored : null;
  } catch {
    return null;
  }
}

export function rememberWorkspace(workspace: string): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, workspace);
  } catch {
    // Signing in still works; it just has to be asked again next time.
  }
}

export function forgetWorkspace(): void {
  try {
    window.localStorage.removeItem(STORAGE_KEY);
  } catch {
    // Nothing to do — the next sign-in overwrites it anyway.
  }
}
