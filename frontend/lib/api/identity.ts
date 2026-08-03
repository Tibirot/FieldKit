/**
 * Who the API thinks the caller is.
 *
 * The token is decided by the API, not by this app: it validates the signature, resolves the issuer
 * against its tenant registry, and re-derives the tenant and permissions from the token it accepted
 * (ADR-0008). Reading them here from a locally-decoded JWT would produce a second opinion — one that
 * cannot enforce anything and can disagree with the one that can.
 */
export type Identity = {
  subject: string;
  tenant: string;
  permissions: string[];
};

/** Raised for a response the caller should act on differently — chiefly 401. */
export class ApiError extends Error {
  constructor(readonly status: number) {
    super(`The API responded ${status}.`);
    this.name = "ApiError";
  }
}

/**
 * Calls the API as the signed-in user.
 *
 * Same-origin `/api/...`, rewritten to the API by `next.config.ts`. That is not incidental: it keeps
 * every call free of CORS preflights, and it is what the service worker already assumes when it
 * refuses to cache anything under `/api/` (`sw/index.js`).
 */
export async function fetchIdentity(accessToken: string, signal?: AbortSignal): Promise<Identity> {
  const response = await fetch("/api/auth/whoami", {
    headers: { Authorization: `Bearer ${accessToken}`, Accept: "application/json" },
    signal,
  });

  if (!response.ok) {
    throw new ApiError(response.status);
  }

  return (await response.json()) as Identity;
}
