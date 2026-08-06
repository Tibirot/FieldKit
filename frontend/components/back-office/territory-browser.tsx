"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { TerritoryAssignments } from "@/components/back-office/territory-assignments";
import { TerritoryOutlets } from "@/components/back-office/territory-outlets";
import { TerritoryForm } from "@/components/back-office/territory-form";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import {
  byId,
  deleteTerritory,
  fetchOrgUnits,
  fetchTerritories,
  orgUnitsKey,
  pathOf,
  territoriesKey,
  type Territory,
} from "@/lib/api/org";
import { usePathname, useRouter } from "@/i18n/navigation";
import { cn } from "@/lib/utils";
import { useSearchParams } from "next/navigation";

/**
 * Territories, and the outlets each one holds (`ORG-03`).
 *
 * **The org-unit filter lives in the URL**, per the client-state decision in ADR-0004 — a filtered
 * view is what someone bookmarks and sends to a colleague, and holding it in React state would make
 * "the territories in Muntenia" unspeakable.
 *
 * The outlet count comes from the server. Counting it here would mean fetching every membership of
 * every territory to render a column of numbers.
 */
export function TerritoryBrowser() {
  const t = useTranslations("Territories");
  const { user } = useAuth();
  const client = useQueryClient();
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const orgUnitId = params.get("orgUnitId") || undefined;

  /** Which territory's detail panel is open — in the URL, so it can be sent to a colleague. */
  const selectedId = params.get("territory") || undefined;

  const units = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: orgUnitsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchOrgUnits(accessToken!, signal),
  });

  const territories = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: territoriesKey(subject ?? "", orgUnitId),
    queryFn: ({ signal }) => fetchTerritories(accessToken!, orgUnitId, signal),
  });

  /** Which form is open: nothing, a new territory, or one being renamed. */
  const [editing, setEditing] = useState<Territory | "new" | null>(null);
  const [refused, setRefused] = useState<readonly string[]>([]);

  const remove = useMutation({
    mutationFn: (territory: Territory) => deleteTerritory(accessToken!, territory.id),
    onSuccess: async () => {
      setRefused([]);
      await client.invalidateQueries({ queryKey: ["territories"] });
    },
    onError: (error) => {
      // The server's own words. "'North' still holds 42 outlets. Move them first." is a refusal an
      // admin can act on; "could not delete" is not.
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? error.problems.map((problem) => problem.message)
          : [t("deleteFailed")],
      );
    },
  });

  function go(changes: Record<string, string | undefined>) {
    const next = new URLSearchParams(params.toString());

    for (const [key, value] of Object.entries(changes)) {
      if (value) next.set(key, value);
      else next.delete(key);
    }

    router.push(`${pathname}${next.size > 0 ? `?${next}` : ""}`);
  }

  const unitsById = byId(units.data ?? []);
  const rows = territories.data ?? [];

  // Resolved from the list rather than fetched: a territory id in the URL that is not in the current
  // view — stale link, changed filter — simply opens nothing, which is the honest outcome.
  const selected = rows.find((territory) => territory.id === selectedId);

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-2">
        <label htmlFor="orgUnitFilter" className="sr-only">
          {t("filterLabel")}
        </label>
        <select
          id="orgUnitFilter"
          value={orgUnitId ?? ""}
          // Narrowing the list drops the open panel: a territory the filter excludes is not one the
          // screen is showing, and leaving its detail below an empty table reads as a bug.
          onChange={(event) => go({ orgUnitId: event.target.value, territory: undefined })}
          className="h-9 rounded-lg border border-input bg-background px-3 text-sm"
        >
          <option value="">{t("allOrgUnits")}</option>
          {(units.data ?? []).map((unit) => (
            <option key={unit.id} value={unit.id}>
              {pathOf(unit, unitsById)}
            </option>
          ))}
        </select>

        <Button type="button" size="sm" onClick={() => setEditing("new")}>
          <Plus className="size-4" />
          {t("newTerritory")}
        </Button>
      </div>

      {editing !== null ? (
        <TerritoryForm
          // Remounted per target, because react-hook-form captures its defaults on the first render
          // — without this, opening Rename on a second territory would show the first one's name.
          key={editing === "new" ? "new" : editing.id}
          territory={editing === "new" ? undefined : editing}
          orgUnits={units.data ?? []}
          onDone={() => setEditing(null)}
          onCancel={() => setEditing(null)}
        />
      ) : null}

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-xl bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      {territories.isPending ? (
        <p className="text-sm text-muted-foreground">{t("loading")}</p>
      ) : territories.isError ? (
        <p role="alert" className="text-sm text-destructive">
          {territories.error instanceof ApiError && territories.error.status === 403
            ? t("forbidden")
            : t("failed")}
        </p>
      ) : rows.length === 0 ? (
        <p className="text-sm text-muted-foreground">{orgUnitId ? t("noMatches") : t("empty")}</p>
      ) : (
        <div className="overflow-x-auto rounded-xl border border-border">
          <table className="w-full text-sm">
            <caption className="sr-only">{t("caption")}</caption>
            <thead className="bg-muted/50 text-xs uppercase">
              <tr>
                <th scope="col" className="px-3 py-2 text-left font-medium">
                  {t("name")}
                </th>
                <th scope="col" className="px-3 py-2 text-left font-medium">
                  {t("orgUnit")}
                </th>
                <th scope="col" className="px-3 py-2 text-right font-medium">
                  {t("outlets")}
                </th>
                <th scope="col" className="px-3 py-2 text-right font-medium">
                  <span className="sr-only">{t("actions")}</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {rows.map((territory) => (
                <tr
                  key={territory.id}
                  className={cn(
                    "border-t border-border",
                    territory.id === selectedId && "bg-muted/40",
                  )}
                >
                  <th scope="row" className="px-3 py-2 text-left font-medium">
                    <button
                      type="button"
                      onClick={() =>
                        go({ territory: territory.id === selectedId ? undefined : territory.id })
                      }
                      aria-expanded={territory.id === selectedId}
                      className="text-left underline-offset-4 hover:underline focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
                    >
                      {territory.name}
                    </button>
                  </th>
                  <td className="px-3 py-2 text-muted-foreground">
                    {unitsById.has(territory.orgUnitId)
                      ? pathOf(unitsById.get(territory.orgUnitId)!, unitsById)
                      : "—"}
                  </td>
                  <td className="px-3 py-2 text-right tabular-nums">{territory.outletCount}</td>
                  <td className="px-3 py-2 text-right">
                    <div className="flex justify-end gap-2">
                      <Button
                        type="button"
                        size="sm"
                        variant="outline"
                        onClick={() => setEditing(territory)}
                        aria-label={t("renameNamed", { name: territory.name })}
                      >
                        {t("rename")}
                      </Button>
                      <Button
                        type="button"
                        size="sm"
                        variant="outline"
                        // Not disabled when it holds outlets: the server's refusal names the count
                        // and says what to do about it, which is more use than a dead button that
                        // explains nothing.
                        disabled={remove.isPending}
                        onClick={() => remove.mutate(territory)}
                        aria-label={t("deleteNamed", { name: territory.name })}
                      >
                        {t("delete")}
                      </Button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/*
        Below the table rather than beside it: the detail is wider than a sidebar wants to be once it
        holds a date-range form, and stacking is what the layout does on a phone anyway.
      */}
      {/*
        Outlets before reps, because that is the order the questions come in: what does this
        territory cover, and then who covers it. A rep assigned to a territory holding nothing is an
        assignment to nothing.
      */}
      {selected ? (
        <>
          <TerritoryOutlets territory={selected} />
          <TerritoryAssignments territory={selected} />
        </>
      ) : null}
    </div>
  );
}
