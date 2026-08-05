/**
 * One thing the API said was wrong, and which part of the request it was about.
 *
 * `field` is the JSON path the caller sent — `code`, `channelId`,
 * `customFields.chiller_count` — or null when the problem is about the request as a whole. A form
 * puts the first kind beside a control and the second at the top.
 */
export type FieldProblem = {
  field: string | null;
  message: string;
};

/**
 * Raised for a response the caller should act on differently — chiefly 400, 401, 403 and 409.
 *
 * `problems` carries what the API said was wrong, when it said anything. A form that discards them
 * has to invent its own explanation for a refusal it could not have predicted — a code taken a
 * second ago, or a rule that only exists in a tenant's catalogue. Empty for the statuses with
 * nothing to add: a 403 is about the caller, not the payload.
 */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problems: readonly FieldProblem[] = [],
  ) {
    super(problems[0]?.message ?? `The API responded ${status}.`);
    this.name = "ApiError";
  }
}

/** Every refusal uses one envelope, whatever its status (api-contracts §3). */
type Refusal = { errors?: FieldProblem[] };

async function refuse(response: Response): Promise<never> {
  let problems: FieldProblem[] = [];

  // A refusal without a readable body is still a refusal — the status is the part that must not be
  // lost, so parsing failures are swallowed rather than replacing one error with another.
  try {
    problems = ((await response.json()) as Refusal).errors ?? [];
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
