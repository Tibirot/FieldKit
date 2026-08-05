"use client";

import { useQuery } from "@tanstack/react-query";
import { Search, X } from "lucide-react";
import { useTranslations } from "next-intl";
import { useEffect, useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { channelsKey, fetchChannels } from "@/lib/api/channels";
import type { OutletQuery, OutletStatus } from "@/lib/api/outlets";
import { cn } from "@/lib/utils";

const STATUSES: readonly OutletStatus[] = ["Active", "Inactive", "Closed"];

const CONTROL =
  "h-9 rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/**
 * How long typing settles before it becomes a request.
 *
 * Long enough that a word is one query rather than five, short enough that the table does not feel
 * detached from the box. Also why search updates the URL with `replace`: at this cadence, `push`
 * would leave a history entry per pause and make the back button walk through half-typed searches.
 */
const SETTLE_MS = 300;

export function OutletFilters({
  query,
  onChange,
}: {
  query: OutletQuery;
  onChange: (next: Partial<OutletQuery>) => void;
}) {
  const t = useTranslations("Outlets");
  const { user } = useAuth();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const channels = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: channelsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchChannels(accessToken!, signal),
  });

  // The box is its own state so it stays responsive while the URL lags behind by a debounce.
  const [typed, setTyped] = useState(query.search ?? "");

  // Re-syncs when the search changes from somewhere *else* — the Clear button, or a back navigation.
  //
  // Adjusted during render rather than in an effect. The effect version is the obvious one and the
  // React Compiler lint refuses it, correctly: it renders once with the stale value, commits, then
  // renders again. This runs before anything is committed, so there is no flash of the old search
  // and no second pass. It has to compare first — assigning unconditionally would overwrite what
  // someone is mid-way through typing.
  const [lastApplied, setLastApplied] = useState(query.search);

  if (query.search !== lastApplied) {
    setLastApplied(query.search);
    setTyped(query.search ?? "");
  }

  useEffect(() => {
    if (typed === (query.search ?? "")) return;

    const timer = setTimeout(() => onChange({ search: typed || undefined }), SETTLE_MS);

    return () => clearTimeout(timer);
  }, [typed, query.search, onChange]);

  const filtered = Boolean(query.search || query.channelId || query.status);

  return (
    <div className="flex flex-wrap items-center gap-2">
      <div className="relative">
        <Search
          aria-hidden="true"
          className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground"
        />
        <input
          type="search"
          value={typed}
          onChange={(event) => setTyped(event.target.value)}
          placeholder={t("searchPlaceholder")}
          aria-label={t("searchLabel")}
          className={cn(CONTROL, "w-56 pl-8")}
        />
      </div>

      <select
        value={query.channelId ?? ""}
        onChange={(event) => onChange({ channelId: event.target.value || undefined })}
        aria-label={t("columns.channel")}
        className={CONTROL}
      >
        <option value="">{t("allChannels")}</option>
        {(channels.data ?? []).map((channel) => (
          <option key={channel.id} value={channel.id}>
            {channel.name}
          </option>
        ))}
      </select>

      <select
        value={query.status ?? ""}
        onChange={(event) =>
          onChange({ status: (event.target.value || undefined) as OutletStatus | undefined })
        }
        aria-label={t("columns.status")}
        className={CONTROL}
      >
        <option value="">{t("allStatuses")}</option>
        {STATUSES.map((status) => (
          <option key={status} value={status}>
            {t(`status.${status}`)}
          </option>
        ))}
      </select>

      {/*
        Only when something is filtered. A permanently visible Clear invites the question "clear
        what?" on a screen showing everything, and it is the control people reach for when a table
        looks wrong — better that its presence is itself the answer.
      */}
      {filtered ? (
        <Button
          variant="ghost"
          size="sm"
          onClick={() => onChange({ search: undefined, channelId: undefined, status: undefined })}
        >
          <X className="size-3.5" />
          {t("clearFilters")}
        </Button>
      ) : null}
    </div>
  );
}
