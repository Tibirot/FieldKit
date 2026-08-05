/**
 * Raised for a response the caller should act on differently — chiefly 401, 403 and 400.
 *
 * `problems` carries what the API said was wrong, when it said anything: our endpoints answer a
 * rejected write with `{ error }` or `{ errors: [...] }`, and a form that discards those has to
 * invent its own explanation for a refusal it did not predict. Empty for the statuses that have
 * nothing to add — a 403 is about the caller, not about the payload.
 */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problems: readonly string[] = [],
  ) {
    super(problems[0] ?? `The API responded ${status}.`);
    this.name = "ApiError";
  }
}

/** The two shapes our endpoints refuse a write with. */
type Refusal = { error?: string; errors?: string[] };

async function refuse(response: Response): Promise<never> {
  let problems: string[] = [];

  // A refusal without a readable body is still a refusal — the status is the part that must not be
  // lost, so parsing failures are swallowed rather than replacing one error with another.
  try {
    const body = (await response.json()) as Refusal;
    problems = body.errors ?? (body.error ? [body.error] : []);
  } catch {
    problems = [];
  }

  throw new ApiError(response.status, problems);
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
    await refuse(response);
  }

  return (await response.json()) as T;
}

/**
 * Sends a write, and returns whatever came back.
 *
 * One function for POST and PUT because the difference is a verb, not a shape. `DELETE` and the
 * 204s will land here too when something needs them — the return type is generic rather than
 * `void`, since a create answers with the thing it created and a form wants to show it.
 */
export async function apiSend<T>(
  method: "POST" | "PUT",
  path: string,
  accessToken: string,
  body: unknown,
  signal?: AbortSignal,
): Promise<T> {
  const response = await fetch(path, {
    method,
    headers: {
      Authorization: `Bearer ${accessToken}`,
      Accept: "application/json",
      "Content-Type": "application/json",
    },
    body: JSON.stringify(body),
    signal,
  });

  if (!response.ok) {
    await refuse(response);
  }

  return (await response.json()) as T;
}
