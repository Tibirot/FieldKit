"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { OrgUnitForm } from "@/components/back-office/org-unit-form";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import {
  byId,
  deleteOrgUnit,
  fetchOrgUnits,
  orgUnitsKey,
  pathOf,
  treeOf,
  type OrgUnit,
} from "@/lib/api/org";
import { usePermissions } from "@/lib/auth/use-permissions";

/**
 * The sales hierarchy (`ORG-01`).
 *
 * **Depth and labels are the tenant's own** — a unit is a name and a parent, and nothing here knows
 * what a "region" is. So it renders as a tree with indentation rather than fixed columns for levels
 * that may not exist.
 *
 * It sits above the territories because it has to: a territory hangs off a unit, so a workspace with
 * no hierarchy cannot have one. That was the state this screen was in until now — the parent picker
 * offered whatever units happened to exist, and nothing could create the first.
 */
export function OrgUnitBrowser() {
  const t = useTranslations("OrgUnits");
  const { user } = useAuth();
  const client = useQueryClient();
  const { has } = usePermissions();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const units = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: orgUnitsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchOrgUnits(accessToken!, signal),
  });

  const [editing, setEditing] = useState<OrgUnit | "new" | null>(null);
  const [refused, setRefused] = useState<readonly string[]>([]);

  const remove = useMutation({
    mutationFn: (unit: OrgUnit) => deleteOrgUnit(accessToken!, unit.id),
    onSuccess: async () => {
      setRefused([]);
      await client.invalidateQueries({ queryKey: ["org-units"] });
    },
    onError: (error) => {
      // Three different refusals, each naming what is in the way: child units, staffed positions, or
      // territories. Replacing them with "could not delete" would throw away the only part an admin
      // can act on.
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? error.problems.map((problem) => problem.message)
          : [t("deleteFailed")],
      );
    },
  });

  const rows = treeOf(units.data ?? []);
  const unitsById = byId(units.data ?? []);

  return (
    <section className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-base font-semibold tracking-tight">{t("title")}</h2>
        {has("orgunit:write") ? (
          <Button type="button" size="sm" onClick={() => setEditing("new")}>
            <Plus className="size-4" />
            {t("newUnit")}
          </Button>
        ) : null}
      </div>

      {editing !== null ? (
        <OrgUnitForm
          // Remounted per target: react-hook-form captures its defaults on the first render.
          key={editing === "new" ? "new" : editing.id}
          unit={editing === "new" ? undefined : editing}
          units={units.data ?? []}
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

      {units.isPending ? (
        <p className="text-sm text-muted-foreground">{t("loading")}</p>
      ) : units.isError ? (
        <p role="alert" className="text-sm text-destructive">
          {units.error instanceof ApiError && units.error.status === 403
            ? t("forbidden")
            : t("failed")}
        </p>
      ) : rows.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t("empty")}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
          {rows.map(({ unit, depth }) => (
            <li key={unit.id} className="flex flex-wrap items-center gap-3 px-4 py-2 text-sm">
              {/*
                Indentation carries the depth on screen; the whole path carries it to anything not
                looking at one. Deliberately not `role="treeitem"` — that promises expand, collapse
                and arrow-key navigation, none of which this list has, and a role that describes a
                widget nobody built is worse than no role.
              */}
              <span
                style={{ paddingInlineStart: `${depth * 1.25}rem` }}
                className="font-medium"
              >
                {depth > 0 ? (
                  <span aria-hidden="true" className="mr-2 text-muted-foreground">
                    └
                  </span>
                ) : null}
                {unit.name}
                {depth > 0 ? (
                  <span className="sr-only">{t("under", { path: pathOf(unit, unitsById) })}</span>
                ) : null}
              </span>

              <div className="ml-auto flex gap-2">
                {has("orgunit:write") ? (
                  <>
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  onClick={() => setEditing(unit)}
                  aria-label={t("editNamed", { name: unit.name })}
                >
                  {t("edit")}
                </Button>
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  disabled={remove.isPending}
                  onClick={() => remove.mutate(unit)}
                  aria-label={t("deleteNamed", { name: unit.name })}
                >
                  {t("delete")}
                </Button>
                  </>
                ) : null}
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
