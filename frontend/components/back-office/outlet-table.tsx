"use client";

import { useQuery } from "@tanstack/react-query";
import { useTranslations } from "next-intl";

import { useAuth } from "@/components/auth-provider";
import { Badge } from "@/components/ui/badge";
import { ApiError } from "@/lib/api/client";
import {
  fetchOutlets,
  outletsKey,
  type OutletQuery,
  type OutletSort,
  type OutletStatus,
} from "@/lib/api/outlets";
import { cn } from "@/lib/utils";

/**
 * Status reads as a colour as well as a word.
 *
 * Semantic, and kept apart from the brand accent on purpose (see the UX design direction): a
 * scanned table has to show what needs attention without being read word by word.
 */
const STATUS_VARIANT: Record<OutletStatus, "default" | "secondary" | "destructive"> = {
  Active: "default",
  Inactive: "secondary",
  Closed: "destructive",
};

/**
 * Which columns can be ordered, and by what.
 *
 * Territory is absent because the server cannot sort by it — it is resolved from Organization
 * after the page is fetched (`ORG-05`), so ordering on it would order the fifty rows already
 * chosen. Segment is absent because the API does not offer it; a header that sorted nothing would
 * be worse than one that never invited the click.
 */
const COLUMNS = ["code", "name", "channel", "segment", "territory", "status"] as const;

type Column = (typeof COLUMNS)[number];

const SORTABLE: Partial<Record<Column, OutletSort>> = {
  code: "Code",
  name: "Name",
  channel: "Channel",
  status: "Status",
};

export function OutletTable({
  query = {},
  onSort,
}: {
  query?: OutletQuery;
  onSort?: (sort: OutletSort) => void;
}) {
  const t = useTranslations("Outlets");
  const { user } = useAuth();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const outlets = useQuery({
    // `enabled` rather than a conditional hook: the shell only renders this when authenticated, but
    // a token can expire between renders and the query must simply not run rather than send
    // `Bearer undefined`.
    enabled: Boolean(accessToken && subject),
    queryKey: outletsKey(subject ?? "", query),
    queryFn: ({ signal }) => fetchOutlets(accessToken!, query, signal),

    // Keeps the previous page on screen while the next one loads, instead of collapsing the table
    // to a spinner and back. Without it, every page change makes the whole layout jump.
    placeholderData: (previous) => previous,
  });

  if (outlets.isPending) {
    return (
      <p className="text-sm text-muted-foreground" role="status">
        {t("loading")}
      </p>
    );
  }

  if (outlets.isError) {
    const forbidden = outlets.error instanceof ApiError && outlets.error.status === 403;

    return (
      <p className="text-sm text-destructive" role="alert">
        {forbidden ? t("forbidden") : t("failed")}
      </p>
    );
  }

  if (outlets.data.total === 0) {
    // Two different facts. "No outlets yet" invites an import, which is the wrong thing to say to
    // someone who has four thousand and mistyped a search — it makes a filtered table look broken.
    const narrowed = Boolean(query.search || query.channelId || query.status);

    return (
      <p className="text-sm text-muted-foreground">{narrowed ? t("noMatches") : t("empty")}</p>
    );
  }

  return (
    <div className="overflow-x-auto">
      <table className="w-full border-collapse text-sm">
        <caption className="sr-only">{t("caption")}</caption>
        <thead>
          <tr className="bg-muted/50 text-left">
            {COLUMNS.map((column) => {
              const sortable = SORTABLE[column];
              const active = sortable !== undefined && (query.sort ?? "Code") === sortable;

              return (
                <th
                  key={column}
                  scope="col"
                  // aria-sort on the header cell is what a screen reader announces, and the only
                  // signal a caret alone does not carry.
                  aria-sort={active ? (query.descending ? "descending" : "ascending") : undefined}
                  className="border-b border-border px-3.5 py-2.5 font-mono text-[10.5px] font-bold tracking-[0.05em] text-muted-foreground uppercase"
                >
                  {sortable && onSort ? (
                    <button
                      type="button"
                      onClick={() => onSort(sortable)}
                      className="flex items-center gap-1 uppercase hover:text-foreground"
                    >
                      {t(`columns.${column}`)}
                      <span aria-hidden="true" className={cn(!active && "opacity-0")}>
                        {query.descending ? "\u2193" : "\u2191"}
                      </span>
                    </button>
                  ) : (
                    t(`columns.${column}`)
                  )}
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>
          {outlets.data.items.map((outlet) => (
            <tr key={outlet.id} className="border-b border-border last:border-b-0">
              <td className="px-3.5 py-2.5 font-mono tabular-nums">{outlet.code}</td>
              <td className="px-3.5 py-2.5 font-semibold">{outlet.name}</td>
              <td className="px-3.5 py-2.5">{outlet.channelName}</td>
              <td className="px-3.5 py-2.5 text-muted-foreground">{outlet.segment ?? "—"}</td>
              <td className="px-3.5 py-2.5">
                {/* Unassigned is an ordinary state, so it reads as a quiet dash rather than a warning. */}
                {outlet.territory?.name ?? (
                  <span className="text-muted-foreground">{t("noTerritory")}</span>
                )}
              </td>
              <td className="px-3.5 py-2.5">
                <Badge variant={STATUS_VARIANT[outlet.status]}>
                  {t(`status.${outlet.status}`)}
                </Badge>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

    </div>
  );
}
