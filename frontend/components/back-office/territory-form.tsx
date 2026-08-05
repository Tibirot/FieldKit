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
  createTerritory,
  pathOf,
  updateTerritory,
  type OrgUnit,
  type Territory,
} from "@/lib/api/org";
import { useValidationMessages } from "@/lib/forms/use-validation-messages";
import { cn } from "@/lib/utils";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

type FieldPath = RhfFieldPath<{ name: string; orgUnitId: string }>;

/**
 * Name a territory and say where it sits (`ORG-03`).
 *
 * One component for create and rename, because they are the same two fields — and an org unit is one
 * of them rather than fixed at creation: territories move between units as a sales organisation is
 * redrawn, which is the ordinary case rather than a migration.
 */
export function TerritoryForm({
  territory,
  orgUnits,
  onDone,
  onCancel,
}: {
  territory?: Territory;
  orgUnits: OrgUnit[];
  onDone: () => void;
  onCancel: () => void;
}) {
  const t = useTranslations("Territories");
  const messages = useValidationMessages();
  const { user } = useAuth();
  const client = useQueryClient();

  const accessToken = user?.access_token;
  const units = byId(orgUnits);

  const form = useForm({
    resolver: zodResolver(
      z.object({
        name: z.string().trim().min(1, { message: messages.required }).max(100, {
          message: messages.tooLong(100),
        }),
        orgUnitId: z.string().min(1, { message: messages.required }),
      }),
    ),
    mode: "onBlur",
    defaultValues: {
      name: territory?.name ?? "",
      orgUnitId: territory?.orgUnitId ?? "",
    },
  });

  const [refused, setRefused] = useState<readonly string[]>([]);

  const save = useMutation({
    mutationFn: (values: { name: string; orgUnitId: string }) =>
      territory
        ? updateTerritory(accessToken!, territory.id, values)
        : createTerritory(accessToken!, values),

    onSuccess: async () => {
      // Every list is now wrong — a rename changes a row and a move takes it out of a filtered view.
      await client.invalidateQueries({ queryKey: ["territories"] });
      onDone();
    },

    onError: (error) => {
      if (!(error instanceof ApiError)) {
        setRefused([t("saveFailed")]);
        return;
      }

      // The same routing the outlet form does: a problem the API attached to a field goes under that
      // control, and anything it could not attribute stays at the top rather than being pinned to a
      // guessed one. "A territory named 'North' already exists" is about the name box.
      const unattributed: string[] = [];

      for (const problem of error.problems) {
        if (problem.field && problem.field in form.getValues()) {
          form.setError(problem.field as FieldPath, { type: "server", message: problem.message });
        } else {
          unattributed.push(problem.message);
        }
      }

      setRefused(unattributed);
    },
  });

  // Rebuilt every render on purpose: react-hook-form writes into the errors object it already has,
  // and the React Compiler memoises this markup on that object's identity — see outlet-form.tsx and
  // frontend-toolchain.md for the bug that taught us.
  const errors = { ...form.formState.errors } as FieldErrors;
  const message = (name: "name" | "orgUnitId") => errors[name]?.message as string | undefined;

  return (
    <form
      onSubmit={form.handleSubmit((values) => {
        setRefused([]);
        save.mutate(values);
      })}
      noValidate
      className="flex flex-col gap-4 rounded-xl border border-border p-4"
    >
      <h2 className="text-sm font-semibold">{territory ? t("renameTitle") : t("newTitle")}</h2>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-lg bg-destructive/10 px-3 py-2 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      <div className="grid gap-4 sm:grid-cols-2">
        <div className="flex flex-col gap-1.5">
          <label htmlFor="name" className="text-sm font-medium">
            {t("name")}
            <span aria-hidden="true" className="ml-1 text-destructive">
              *
            </span>
          </label>
          <input
            {...form.register("name")}
            id="name"
            maxLength={100}
            aria-invalid={Boolean(errors.name)}
            aria-describedby={errors.name ? "name-error" : undefined}
            className={cn(CONTROL, errors.name && "border-destructive")}
          />
          {message("name") ? (
            <p id="name-error" className="text-xs text-destructive">
              {message("name")}
            </p>
          ) : null}
        </div>

        <div className="flex flex-col gap-1.5">
          <label htmlFor="orgUnitId" className="text-sm font-medium">
            {t("orgUnit")}
            <span aria-hidden="true" className="ml-1 text-destructive">
              *
            </span>
          </label>
          <select
            {...form.register("orgUnitId")}
            id="orgUnitId"
            aria-invalid={Boolean(errors.orgUnitId)}
            aria-describedby={errors.orgUnitId ? "orgUnitId-error" : undefined}
            className={cn(CONTROL, errors.orgUnitId && "border-destructive")}
          >
            <option value="" disabled />
            {orgUnits.map((unit) => (
              // The whole path, because a flat list of leaf names is ambiguous the moment two
              // regions each have a "North" — which is the normal case, not the odd one.
              <option key={unit.id} value={unit.id}>
                {pathOf(unit, units)}
              </option>
            ))}
          </select>
          {message("orgUnitId") ? (
            <p id="orgUnitId-error" className="text-xs text-destructive">
              {message("orgUnitId")}
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
