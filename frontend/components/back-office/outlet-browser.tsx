"use client";

import { useQuery } from "@tanstack/react-query";
import { useSearchParams } from "next/navigation";
import { useCallback } from "react";

import { useAuth } from "@/components/auth-provider";
import { OutletFilters } from "@/components/back-office/outlet-filters";
import { OutletTable } from "@/components/back-office/outlet-table";
import { Pager } from "@/components/back-office/pager";
import { usePathname, useRouter } from "@/i18n/navigation";
import {
  fetchOutlets,
  outletsKey,
  type OutletQuery,
  type OutletSort,
  type OutletStatus,
} from "@/lib/api/outlets";

const SORTS: readonly OutletSort[] = ["Code", "Name", "Channel", "Status"];
const STATUSES: readonly OutletStatus[] = ["Active", "Inactive", "Closed"];

/**
 * Reads the query out of the URL.
 *
 * **Validated, not trusted.** These values are typed by whoever is holding the address bar, and an
 * unknown `sort` would go to the API as a value its enum does not have. Anything unrecognised is
 * dropped rather than passed along or 400'd — a mangled URL should show the outlet base, not an
 * error page.
 */
function readQuery(params: URLSearchParams): OutletQuery {
  const sort = params.get("sort");
  const status = params.get("status");
  const page = Number(params.get("page"));

  return {
    search: params.get("search") || undefined,
    channelId: params.get("channelId") || undefined,
    status: STATUSES.includes(status as OutletStatus) ? (status as OutletStatus) : undefined,
    sort: SORTS.includes(sort as OutletSort) ? (sort as OutletSort) : undefined,
    descending: params.get("descending") === "true" || undefined,
    page: Number.isInteger(page) && page > 1 ? page : undefined,
  };
}

/**
 * The outlet base, and the controls over it (<c>OUT-01</c>).
 *
 * **The query lives in the URL**, per the client-state decision in ADR-0004: it is shared across
 * nothing, but it is the state a person wants to bookmark, send to a colleague, and get back after a
 * reload. Keeping it in React state instead would make "the outlets I was looking at" unspeakable.
 */
export function OutletBrowser() {
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const { user } = useAuth();

  const query = readQuery(new URLSearchParams(params.toString()));

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  /**
   * Writes a change back to the URL, which is the only place this state lives.
   *
   * <b>Typing replaces; clicking pushes.</b> A debounced search would otherwise leave a history
   * entry per pause and make Back walk through half-typed words, while choosing a filter or turning
   * a page is a navigation someone reasonably expects to undo.
   */
  const update = useCallback(
    (next: Partial<OutletQuery>, navigate: "push" | "replace") => {
      const merged = { ...query, ...next };
      const search = new URLSearchParams();

      for (const [key, value] of Object.entries(merged)) {
        if (value !== undefined && value !== "" && value !== false) search.set(key, String(value));
      }

      // Any change to what is being looked at resets to the first page. Staying on page 7 while
      // narrowing to twelve results shows an empty table and reads as "the filter broke".
      if (next.page === undefined) search.delete("page");

      const url = search.size > 0 ? `${pathname}?${search}` : pathname;

      router[navigate](url);
    },
    [query, pathname, router],
  );

  const filters = useCallback(
    (next: Partial<OutletQuery>) => update(next, next.search !== undefined ? "replace" : "push"),
    [update],
  );

  const sort = useCallback(
    (column: OutletSort) =>
      // Clicking the column you are already sorted by reverses it; clicking a different one starts
      // ascending. Anything else means a first click can produce a descending list nobody asked for.
      update(
        {
          sort: column,
          descending: (query.sort ?? "Code") === column ? !query.descending : undefined,
        },
        "push",
      ),
    [update, query.sort, query.descending],
  );

  // Read here as well as in the table, so the pager knows the total. Same key and same arguments, so
  // TanStack serves both from one cache entry and one request.
  const outlets = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: outletsKey(subject ?? "", query),
    queryFn: ({ signal }) => fetchOutlets(accessToken!, query, signal),
    placeholderData: (previous) => previous,
  });

  return (
    <div className="flex flex-col gap-3">
      <OutletFilters query={query} onChange={filters} />

      <div className="overflow-hidden rounded-xl border border-border">
        <OutletTable query={query} onSort={sort} />

        {outlets.data && outlets.data.total > 0 ? (
          <Pager
            page={outlets.data.page}
            pageSize={outlets.data.pageSize}
            total={outlets.data.total}
            onChange={(page) => update({ page: page > 1 ? page : undefined }, "push")}
          />
        ) : null}
      </div>
    </div>
  );
}
