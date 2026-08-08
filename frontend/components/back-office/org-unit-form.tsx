"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import { useForm, type FieldErrors, type FieldPath as RhfFieldPath } from "react-hook-form";
import { z } from "zod";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import {
  byId,
  createOrgUnit,
  isDescendantOf,
  pathOf,
  updateOrgUnit,
  type OrgUnit,
} from "@/lib/api/org";
import { useValidationMessages } from "@/lib/forms/use-validation-messages";
import { cn } from "@/lib/utils";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

type Values = { name: string; parentId: string };
type FieldPath = RhfFieldPath<Values>;

/**
 * Name a level of the sales hierarchy and say where it hangs (`ORG-01`).
 *
 * **Depth and labels are the tenant's.** Nothing here knows what a "region" is — a unit is a name and
 * a parent, and a tenant that runs Country → Region → Area → Team gets four levels for the same
 * reason one that runs two gets two.
 *
 * **Rename and reparent are one edit**, because the API made them one call: splitting them would let
 * "rename this team and move it under the new region" half-succeed.
 */
export function OrgUnitForm({
  unit,
  units,
  onDone,
  onCancel,
}: {
  unit?: OrgUnit;
  units: OrgUnit[];
  onDone: () => void;
  onCancel: () => void;
}) {
  const t = useTranslations("OrgUnits");
  const messages = useValidationMessages();
  const { user } = useAuth();
  const client = useQueryClient();

  const accessToken = user?.access_token;
  const unitsById = byId(units);

  const form = useForm({
    resolver: zodResolver(
      z.object({
        name: z.string().trim().min(1, { message: messages.required }).max(200, {
          message: messages.tooLong(200),
        }),

        // Empty is a root, not a missing answer — the top of a hierarchy has no parent.
        parentId: z.string(),
      }),
    ),
    mode: "onBlur",
    defaultValues: { name: unit?.name ?? "", parentId: unit?.parentId ?? "" },
  });

  const [refused, setRefused] = useState<readonly string[]>([]);

  const save = useMutation({
    mutationFn: (values: Values) => {
      const body = { name: values.name, parentId: values.parentId || null };

      return unit ? updateOrgUnit(accessToken!, unit.id, body) : createOrgUnit(accessToken!, body);
    },

    onSuccess: async () => {
      // Territories name their unit by its whole path, so moving one changes rows on a list this
      // form does not own.
      await client.invalidateQueries({ queryKey: ["org-units"] });
      await client.invalidateQueries({ queryKey: ["territories"] });
      onDone();
    },

    onError: (error) => {
      if (!(error instanceof ApiError)) {
        setRefused([t("saveFailed")]);
        return;
      }

      const unattributed: string[] = [];

      for (const problem of error.problems) {
        if (problem.field && problem.field in form.getValues()) {
          form.setError(problem.field as FieldPath, { type: "server", message: problem.message });
        } else {
          unattributed.push(problem.message);
        }
      }

      // A refusal the API attached to nothing — a 403, a 404, a 500 with no body — still has to say
      // something. Without this the loop above runs zero times and the screen goes silent, which reads
      // as a Save button that does nothing rather than as a refusal.
      setRefused(error.problems.length > 0 ? unattributed : [t("saveFailed")]);
    },
  });

  // Rebuilt every render: react-hook-form writes into the errors object it already has, and the
  // React Compiler memoises this markup on that object's identity (frontend-toolchain.md).
  const errors = { ...form.formState.errors } as FieldErrors;

  // A unit cannot hang under itself or under anything below it. The API refuses the cycle; not
  // offering the choice is better, because unlike a name collision this is never what somebody
  // meant — there is no version of "move Romania under Team North" that is a good idea.
  const parents = unit
    ? units.filter((candidate) => !isDescendantOf(candidate, unit.id, unitsById))
    : units;

  return (
    <form
      onSubmit={form.handleSubmit((values) => {
        setRefused([]);
        save.mutate(values);
      })}
      noValidate
      className="flex flex-col gap-4 rounded-xl border border-border p-4"
    >
      <h2 className="text-sm font-semibold">{unit ? t("editTitle") : t("newTitle")}</h2>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-lg bg-destructive/10 px-3 py-2 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      <div className="grid gap-4 sm:grid-cols-2">
        <div className="flex flex-col gap-1.5">
          <label htmlFor="unitName" className="text-sm font-medium">
            {t("name")}
          </label>
          <input
            {...form.register("name")}
            id="unitName"
            maxLength={200}
            aria-invalid={Boolean(errors.name)}
            aria-describedby={errors.name ? "unitName-error" : undefined}
            className={cn(CONTROL, errors.name && "border-destructive")}
          />
          {errors.name ? (
            <p id="unitName-error" className="text-xs text-destructive">
              {errors.name.message as string}
            </p>
          ) : null}
        </div>

        <div className="flex flex-col gap-1.5">
          <label htmlFor="parentId" className="text-sm font-medium">
            {t("parent")}
          </label>
          <select
            {...form.register("parentId")}
            id="parentId"
            aria-invalid={Boolean(errors.parentId)}
            aria-describedby={errors.parentId ? "parentId-error" : undefined}
            className={cn(CONTROL, errors.parentId && "border-destructive")}
          >
            <option value="">{t("noParent")}</option>
            {parents.map((candidate) => (
              <option key={candidate.id} value={candidate.id}>
                {pathOf(candidate, unitsById)}
              </option>
            ))}
          </select>
          {errors.parentId ? (
            <p id="parentId-error" className="text-xs text-destructive">
              {errors.parentId.message as string}
            </p>
          ) : null}
        </div>
      </div>

      <div className="flex gap-2">
        <Button type="submit" size="sm" disabled={save.isPending}>
          {save.isPending ? t("saving") : t("save")}
        </Button>
        <Button type="button" size="sm" variant="outline" onClick={onCancel}>
          {t("cancel")}
        </Button>
      </div>
    </form>
  );
}
