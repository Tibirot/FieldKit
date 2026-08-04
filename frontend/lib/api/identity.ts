import { apiGet } from "@/lib/api/client";

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

/**
 * Calls `/api/auth/whoami` as the signed-in user.
 *
 * The fetch and the error type moved to `lib/api/client.ts` when a second caller appeared. One
 * `ApiError` rather than two identical ones, so `instanceof` keeps meaning what it looks like it
 * means — two same-named classes is the duplication that reads as harmless right up until a retry
 * predicate silently stops matching.
 */
export function fetchIdentity(accessToken: string, signal?: AbortSignal): Promise<Identity> {
  return apiGet<Identity>("/api/auth/whoami", accessToken, signal);
}
