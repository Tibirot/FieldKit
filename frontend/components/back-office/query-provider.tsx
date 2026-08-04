"use client";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useState } from "react";

import { ApiError } from "@/lib/api/client";

/**
 * The query cache for the back office.
 *
 * Built in `useState` rather than at module scope. A module-level client is shared by every request
 * the Next server handles, which on a server render would hand one tenant's data to another — the
 * mistake is invisible in development, where there is one user.
 */
export function QueryProvider({ children }: { children: React.ReactNode }) {
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
             * three times delays the sign-in redirect by exactly as long as the backoff, and a 403
             * is a permanent answer about what this user may do.
             */
            retry: (failureCount, error) =>
              !(error instanceof ApiError && error.status >= 400 && error.status < 500)
              && failureCount < 2,
          },
        },
      }),
  );

  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}
