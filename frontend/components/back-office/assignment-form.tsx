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
import { createAssignment, updateAssignment, type RepAssignment } from "@/lib/api/org";
import { identifying, type User } from "@/lib/api/users";
import { useValidationMessages } from "@/lib/forms/use-validation-messages";
import { cn } from "@/lib/utils";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

type Values = { userId: string; from: string; to: string };
type FieldPath = RhfFieldPath<Values>;

/**
 * Put a rep on a territory for a period (`ORG-04`).
 *
 * **An end date is optional and means "until further notice".** Requiring one would make every
 * assignment a fixed-term contract, and the ordinary case is a rep who covers a territory until
 * somebody decides otherwise.
 *
 * **Overlap is the server's rule** (`BR-ORG-2`) and is not re-checked here. Two people can be
 * editing the same territory, so a client-side answer is a guess about a set it does not own — and
 * the server's refusal already names the period it collided with.
 */
export function AssignmentForm({
  territoryId,
  assignment,
  users,
  onDone,
  onCancel,
}: {
  territoryId: string;
  assignment?: RepAssignment;
  users: User[];
  onDone: () => void;
  onCancel: () => void;
}) {
  const t = useTranslations("Territories");
  const messages = useValidationMessages();
  const { user } = useAuth();
  const client = useQueryClient();

  const accessToken = user?.access_token;

  const form = useForm({
    resolver: zodResolver(
      z.object({
        userId: z.string().min(1, { message: messages.required }),
        from: z.string().min(1, { message: messages.required }),

        // Empty is the open-ended case, not a missing answer, so it validates as a whole rather than
        // as a required field with an exception.
        to: z.string(),
      }),
    ),
    mode: "onBlur",
    defaultValues: {
      userId: assignment?.userId ?? "",
      from: assignment?.from ?? "",
      to: assignment?.to ?? "",
    },
  });

  const [refused, setRefused] = useState<readonly string[]>([]);

  const save = useMutation({
    mutationFn: (values: Values) => {
      const body = { userId: values.userId, from: values.from, to: values.to || null };

      return assignment
        ? updateAssignment(accessToken!, assignment.id, body)
        : createAssignment(accessToken!, territoryId, body);
    },

    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ["assignments"] });
      onDone();
    },

    onError: (error) => {
      if (!(error instanceof ApiError)) {
        setRefused([t("assignFailed")]);
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
      setRefused(error.problems.length > 0 ? unattributed : [t("assignFailed")]);
    },
  });

  // Rebuilt every render: react-hook-form writes into the errors object it already has, and the
  // React Compiler memoises this markup on that object's identity (frontend-toolchain.md).
  const errors = { ...form.formState.errors } as FieldErrors;
  const message = (name: keyof Values) => errors[name]?.message as string | undefined;

  /** A control, its label, and the error wiring between them — three fields, one shape. */
  function bind(name: keyof Values) {
    return {
      ...form.register(name),
      id: name,
      "aria-invalid": Boolean(errors[name]),
      "aria-describedby": errors[name] ? `${name}-error` : undefined,
      className: cn(CONTROL, errors[name] && "border-destructive"),
    };
  }

  return (
    <form
      onSubmit={form.handleSubmit((values) => {
        setRefused([]);
        save.mutate(values);
      })}
      noValidate
      className="flex flex-col gap-4 rounded-lg border border-border p-3"
    >
      {refused.length > 0 ? (
        <ul role="alert" className="rounded-lg bg-destructive/10 px-3 py-2 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      <div className="grid gap-3 sm:grid-cols-3">
        <div className="flex flex-col gap-1.5">
          <label htmlFor="userId" className="text-sm font-medium">
            {t("rep")}
          </label>
          <select {...bind("userId")}>
            <option value="" disabled />
            {users.map((candidate) => (
              // The Keycloak subject, not the profile's row id. They are different strings, and the
              // wrong one comes back as "No such user in this tenant" — a message that reads like a
              // missing person rather than a mismatched identifier.
              <option key={candidate.subjectId} value={candidate.subjectId}>
                {identifying(candidate)}
              </option>
            ))}
          </select>
          {message("userId") ? (
            <p id="userId-error" className="text-xs text-destructive">
              {message("userId")}
            </p>
          ) : null}
        </div>

        <div className="flex flex-col gap-1.5">
          <label htmlFor="from" className="text-sm font-medium">
            {t("from")}
          </label>
          <input {...bind("from")} type="date" />
          {message("from") ? (
            <p id="from-error" className="text-xs text-destructive">
              {message("from")}
            </p>
          ) : null}
        </div>

        <div className="flex flex-col gap-1.5">
          <label htmlFor="to" className="text-sm font-medium">
            {t("to")}
          </label>
          <input {...bind("to")} type="date" />
          <p className="text-xs text-muted-foreground">{t("toHint")}</p>
          {message("to") ? (
            <p id="to-error" className="text-xs text-destructive">
              {message("to")}
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
