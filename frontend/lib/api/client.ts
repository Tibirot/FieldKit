/** Raised for a response the caller should act on differently — chiefly 401 and 403. */
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
 *
 * The token is passed in rather than read from a module-level store, because there is exactly one
 * place that knows whether a session is live — `useAuth` — and a second copy of that answer is a
 * copy that can be stale while looking authoritative.
 */
export async function apiGet<T>(path: string, accessToken: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(path, {
    headers: { Authorization: `Bearer ${accessToken}`, Accept: "application/json" },
    signal,
  });

  if (!response.ok) {
    throw new ApiError(response.status);
  }

  return (await response.json()) as T;
}
