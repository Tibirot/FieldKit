"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import {
  assignOutlets,
  fetchTerritoryOutlets,
  removeOutlet,
  territoryOutletsKey,
  type Territory,
} from "@/lib/api/org";
import { fetchOutlets, outletsKey } from "@/lib/api/outlets";
import { cn } from "@/lib/utils";

/**
 * The outlets a territory covers (`ORG-05`).
 *
 * **Here rather than on the outlet form**, and that is a boundary decision rather than a layout one:
 * membership is Organization's fact, and having Outlets write it for convenience is what module
 * boundaries exist to prevent. The outlet list *reads* the territory through `ITerritoryDirectory`;
 * this is the only place that writes it.
 *
 * **An outlet belongs to exactly one territory.** Reassignment is refused rather than performed
 * silently, because it changes which rep serves the shop and what their device downloads tomorrow
 * morning — so moving one is remove-then-add, and the server names the outlets already taken.
 */
export function TerritoryOutlets({ territory }: { territory: Territory }) {
  const t = useTranslations("Territories");
  const { user } = useAuth();
  const client = useQueryClient();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const members = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: territoryOutletsKey(subject ?? "", territory.id),
    queryFn: ({ signal }) => fetchTerritoryOutlets(accessToken!, territory.id, signal),
  });

  const [search, setSearch] = useState("");
  const [picked, setPicked] = useState<ReadonlySet<string>>(new Set());
  const [refused, setRefused] = useState<readonly string[]>([]);

  // Searched rather than listed. A tenant has thousands of outlets and an admin adding a handful
  // knows what they are looking for; a picker holding the whole base is a scroll, not a choice.
  const matches = useQuery({
    enabled: Boolean(accessToken && subject) && search.trim().length > 0,
    queryKey: outletsKey(subject ?? "", { search: search.trim(), pageSize: 10 }),
    queryFn: ({ signal }) =>
      fetchOutlets(accessToken!, { search: search.trim(), pageSize: 10 }, signal),
  });

  /** Both lists change together, and so does the count on the territory row. */
  async function refresh() {
    await client.invalidateQueries({ queryKey: ["territory-outlets"] });
    await client.invalidateQueries({ queryKey: ["territories"] });
    await client.invalidateQueries({ queryKey: ["outlets"] });
  }

  const keep = (error: unknown, fallback: string) =>
    setRefused(
      error instanceof ApiError && error.problems.length > 0
        ? error.problems.map((problem) => problem.message)
        : [fallback],
    );

  const add = useMutation({
    mutationFn: () => assignOutlets(accessToken!, territory.id, [...picked]),
    onSuccess: async () => {
      setRefused([]);
      setPicked(new Set());
      setSearch("");
      await refresh();
    },
    onError: (error) => keep(error, t("assignOutletsFailed")),
  });

  const drop = useMutation({
    mutationFn: (outletId: string) => removeOutlet(accessToken!, territory.id, outletId),
    onSuccess: async () => {
      setRefused([]);
      await refresh();
    },
    onError: (error) => keep(error, t("removeOutletFailed")),
  });

  const held = new Set((members.data ?? []).map((outlet) => outlet.outletId));

  function toggle(outletId: string, include: boolean) {
    setPicked((current) => {
      const next = new Set(current);

      if (include) next.add(outletId);
      else next.delete(outletId);

      return next;
    });
  }

  return (
    <section className="flex flex-col gap-3 rounded-xl border border-border p-4">
      <h2 className="text-sm font-semibold">{t("outletsIn", { name: territory.name })}</h2>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-lg bg-destructive/10 px-3 py-2 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      {members.isPending ? (
        <p className="text-sm text-muted-foreground">{t("loadingOutlets")}</p>
      ) : members.isError ? (
        <p role="alert" className="text-sm text-destructive">
          {t("outletsFailed")}
        </p>
      ) : members.data?.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t("noOutlets")}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-border">
          {(members.data ?? []).map((outlet) => (
            <li key={outlet.outletId} className="flex flex-wrap items-center gap-3 py-2 text-sm">
              {/*
                Null when the outlet no longer resolves. The membership is real, so the row stays —
                hiding it would make the territory quietly smaller than its own count says, and
                nobody would know which shop went missing.
              */}
              <span className="font-mono text-xs">{outlet.code ?? t("unknownOutlet")}</span>
              <span className={cn(outlet.isOpen === false && "text-muted-foreground line-through")}>
                {outlet.name ?? "—"}
              </span>
              {outlet.isOpen === false ? (
                <span className="rounded-full bg-muted px-2 py-0.5 text-xs text-muted-foreground">
                  {t("closed")}
                </span>
              ) : null}

              <Button
                type="button"
                size="sm"
                variant="outline"
                className="ml-auto"
                disabled={drop.isPending}
                onClick={() => drop.mutate(outlet.outletId)}
                aria-label={t("removeOutletNamed", { code: outlet.code ?? outlet.outletId })}
              >
                {t("removeOutlet")}
              </Button>
            </li>
          ))}
        </ul>
      )}

      <div className="flex flex-col gap-2 rounded-lg border border-border p-3">
        <label htmlFor="outletSearch" className="text-sm font-medium">
          {t("addOutlets")}
        </label>
        <input
          id="outletSearch"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
          placeholder={t("outletSearchPlaceholder")}
          className="h-9 w-full rounded-lg border border-input bg-background px-3 text-sm focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
        />

        {search.trim().length === 0 ? null : matches.isPending ? (
          <p className="text-xs text-muted-foreground">{t("searching")}</p>
        ) : (matches.data?.items.length ?? 0) === 0 ? (
          <p className="text-xs text-muted-foreground">{t("noOutletMatches")}</p>
        ) : (
          <ul className="flex flex-col gap-1">
            {(matches.data?.items ?? []).map((outlet) => (
              <li key={outlet.id}>
                <label className="flex items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    checked={picked.has(outlet.id)}
                    // Already in this territory: offered as ticked-and-fixed rather than hidden, so
                    // a search that finds the shop someone expects does not silently omit it.
                    disabled={held.has(outlet.id)}
                    onChange={(event) => toggle(outlet.id, event.target.checked)}
                    className="size-4 accent-primary"
                  />
                  <span className="font-mono text-xs">{outlet.code}</span>
                  <span>{outlet.name}</span>
                  {held.has(outlet.id) ? (
                    <span className="text-xs text-muted-foreground">{t("alreadyHere")}</span>
                  ) : outlet.territory ? (
                    // Named before the attempt. The server refuses a reassignment and says which
                    // outlets to free up first, but knowing beforehand is the difference between a
                    // choice and a correction.
                    <span className="text-xs text-muted-foreground">
                      {t("inTerritory", { name: outlet.territory.name })}
                    </span>
                  ) : null}
                </label>
              </li>
            ))}
          </ul>
        )}

        {picked.size > 0 ? (
          <div>
            <Button type="button" size="sm" disabled={add.isPending} onClick={() => add.mutate()}>
              {add.isPending ? t("adding") : t("addPicked", { count: picked.size })}
            </Button>
          </div>
        ) : null}
      </div>
    </section>
  );
}
