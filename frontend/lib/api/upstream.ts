/**
 * Where `/api/*` goes, resolved **per request** rather than at build time.
 *
 * The browser always calls the API same-origin (`lib/api/client.ts` sends `/api/...`, the service
 * worker refuses to cache that prefix by pathname). Something has to turn that into the API's real
 * address, and until now that something was a `rewrites()` entry in `next.config.ts`.
 *
 * **`rewrites()` is evaluated by `next build`, not by the running server.** Its output is frozen
 * into `.next/routes-manifest.json`, so an image built in CI — where no `services__server__*`
 * variable exists — ships `"rewrites": {"beforeFiles": [], "afterFiles": [], "fallback": []}` and
 * answers every API call with the app's own 404 page. Setting the variable on the *container* does
 * not help: the manifest was written an hour earlier on a build agent. Verified by running the
 * standalone server with the variable set and getting a 404 anyway.
 *
 * It worked in development for the reason it fails in a container: `next dev` re-evaluates
 * `next.config.ts`, and Aspire has already put the address in the environment by then. A
 * configuration mistake that only exists in the artifact you deploy is the expensive kind.
 *
 * So the mapping moves to `proxy.ts`, which runs per request and reads the environment then. This
 * module is the part worth asserting about, kept pure so it can be.
 */

/**
 * Aspire's service-discovery keys for the API, in the order they are preferred.
 *
 * HTTPS first, matching what `next.config.ts` did before this and what `lib/auth/settings.ts` does
 * for Keycloak — changing the preference here would change which address development uses, which is
 * not this slice's business.
 */
const SERVICE_KEYS = ["services__server__https__0", "services__server__http__0"] as const;

/**
 * The API's origin, or null when nothing names it.
 *
 * Null is a real state rather than a misconfiguration: `next build` runs with no API in existence,
 * and the build must not fail because of it.
 */
export function apiOrigin(env: NodeJS.ProcessEnv = process.env): string | null {
  const candidate = SERVICE_KEYS.map((key) => env[key]).find(Boolean) ?? env.API_URL;
  if (!candidate) return null;

  try {
    // Origin only, deliberately. Aspire hands over a bare `scheme://host:port`, and taking the
    // origin means a stray trailing slash or path in `API_URL` cannot produce `//api/...` upstream.
    // A base-path deployment of the API is therefore not supported here; nothing deploys one.
    return new URL(candidate).origin;
  } catch {
    return null;
  }
}

/**
 * The upstream URL for an inbound `/api/...` request, or null when no API is configured.
 *
 * The path is passed through **unchanged**, including the `/api` prefix: the server mounts its
 * routes under `/api` too (`api-contracts §1`), so this is a change of host and nothing else.
 */
export function upstreamUrl(
  pathname: string,
  search: string,
  env: NodeJS.ProcessEnv = process.env,
): URL | null {
  const origin = apiOrigin(env);
  if (!origin) return null;

  return new URL(`${pathname}${search}`, origin);
}
