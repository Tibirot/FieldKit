"use client";

import { useQuery } from "@tanstack/react-query";
import { useTranslations } from "next-intl";

import { useAuth } from "@/components/auth-provider";
import { Badge } from "@/components/ui/badge";
import { ApiError } from "@/lib/api/client";
import { fetchOutlets, outletsKey, type OutletStatus } from "@/lib/api/outlets";

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

export function OutletTable() {
  const t = useTranslations("Outlets");
  const { user } = useAuth();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const outlets = useQuery({
    // `enabled` rather than a conditional hook: the shell only renders this when authenticated, but
    // a token can expire between renders and the query must simply not run rather than send
    // `Bearer undefined`.
    enabled: Boolean(accessToken && subject),
    queryKey: outletsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchOutlets(accessToken!, signal),
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

  if (outlets.data.length === 0) {
    return <p className="text-sm text-muted-foreground">{t("empty")}</p>;
  }

  return (
    <div className="overflow-x-auto rounded-xl border border-border">
      <table className="w-full border-collapse text-sm">
        <caption className="sr-only">{t("caption")}</caption>
        <thead>
          <tr className="bg-muted/50 text-left">
            {(["code", "name", "channel", "segment", "territory", "status"] as const).map(
              (column) => (
                <th
                  key={column}
                  scope="col"
                  className="border-b border-border px-3.5 py-2.5 font-mono text-[10.5px] font-bold tracking-[0.05em] text-muted-foreground uppercase"
                >
                  {t(`columns.${column}`)}
                </th>
              ),
            )}
          </tr>
        </thead>
        <tbody>
          {outlets.data.map((outlet) => (
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
