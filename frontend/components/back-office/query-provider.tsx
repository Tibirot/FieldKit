"use client";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useEffect, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { ApiError } from "@/lib/api/client";

/**
 * The query cache for the back office.
 *
 * Built in `useState` rather than at module scope. A module-level client is shared by every request
 * the Next server handles, which on a server render would hand one tenant's data to another — the
 * mistake is invisible in development, where there is one user.
 */
export function QueryProvider({ children }: { children: React.ReactNode }) {
  const { expire } = useAuth();

  const [client] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: {
            /**
             * Master data changes when someone in the back office changes it, not on its own. Half a
             * minute of staleness costs nothing and stops every tab focus re-fetching the outlet
             * base — this is a desktop console, not a live feed.
             */
            staleTime: 30_000,

            /**
             * Never retry a 401 or a 403. A rejected token is not a flaky network: retrying it
             * three times delays the re-auth prompt by exactly as long as the backoff, and a 403
             * is a permanent answer about what this user may do.
             */
            retry: (failureCount, error) =>
              !(error instanceof ApiError && error.status >= 400 && error.status < 500)
              && failureCount < 2,
          },
        },
      }),
  );

  /**
   * A 401 is the API saying the token is no longer good — the one error in this app that is about
   * the session rather than about the request.
   *
   * Watched centrally because it can arrive from anywhere and means the same thing everywhere. The
   * failure this replaces was quiet: `/api/auth/whoami` 401s, `usePermissions` falls back to an
   * empty set, and every gated control disappears while the shell keeps rendering as though signed
   * in. Nobody was watching for it, because there was no obvious place to watch from.
   *
   * Subscribed in an effect rather than handed to the caches as `onError` at construction. The
   * client is built once, on first render, so a handler baked in then closes over that render's
   * `expire` forever — correct today, and silently stale the day `expire` grows a dependency. This
   * re-binds whenever it changes.
   *
   * Mutations as well as queries, and arguably more urgently: a rep whose token died mid-form is
   * about to be told their save failed, with no hint that signing in again is the fix.
   */
  useEffect(() => {
    const endSessionOn401 = (error: unknown) => {
      // `expire` is idempotent, which matters here — one dead token produces a 401 per in-flight
      // query, and a screen with four of them reports four failures for one expiry.
      if (error instanceof ApiError && error.status === 401) {
        expire();
      }
    };

    const unsubscribeQueries = client.getQueryCache().subscribe((event) => {
      if (event.type === "updated" && event.action.type === "error") {
        endSessionOn401(event.action.error);
      }
    });

    const unsubscribeMutations = client.getMutationCache().subscribe((event) => {
      if (event.type === "updated" && event.action.type === "error") {
        endSessionOn401(event.action.error);
      }
    });

    return () => {
      unsubscribeQueries();
      unsubscribeMutations();
    };
  }, [client, expire]);

  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}
