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
  createRole,
  resourceOf,
  updateRole,
  type Permission,
  type Role,
  type RoleWrite,
} from "@/lib/api/users";
import { useValidationMessages } from "@/lib/forms/use-validation-messages";
import { cn } from "@/lib/utils";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

type Values = { name: string; permissions: string[] };
type FieldPath = RhfFieldPath<Values>;

/**
 * Compose a role out of the permissions the system actually enforces (`IAM-04`).
 *
 * **Every permission is a checkbox with its description beside it.** The catalogue is code — a
 * permission exists because some module checks it — so this list cannot offer something that grants
 * nothing. The description is the point: `outlet:write` tells an admin the shape of a grant, not
 * what it lets a person do.
 *
 * **A role may have no permissions at all**, and that is a real thing to want — a role that names a
 * group without granting it anything yet. `BR-IAM-3` is about a *user* holding a role, which is a
 * different rule and lives on the user form.
 */
export function RoleForm({
  role,
  permissions,
  onDone,
  onCancel,
}: {
  role?: Role;
  permissions: Permission[];
  onDone: () => void;
  onCancel: () => void;
}) {
  const t = useTranslations("Roles");
  const messages = useValidationMessages();
  const { user } = useAuth();
  const client = useQueryClient();

  const accessToken = user?.access_token;

  const form = useForm({
    resolver: zodResolver(
      z.object({
        name: z.string().trim().min(1, { message: messages.required }).max(100, {
          message: messages.tooLong(100),
        }),
        permissions: z.array(z.string()),
      }),
    ),
    mode: "onBlur",
    defaultValues: { name: role?.name ?? "", permissions: role?.permissions ?? [] },
  });

  const [refused, setRefused] = useState<readonly string[]>([]);

  const save = useMutation({
    mutationFn: (values: RoleWrite) =>
      role ? updateRole(accessToken!, role.id, values) : createRole(accessToken!, values),

    onSuccess: async () => {
      // Users too: the list names each person's roles, and a rename would leave it showing the old
      // one until something else happened to refetch.
      await client.invalidateQueries({ queryKey: ["roles"] });
      await client.invalidateQueries({ queryKey: ["users"] });
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

  // Grouped by resource, in the order the catalogue came in — which is the server's, alphabetical by
  // name, so `outlet:read` and `outlet:write` are already adjacent.
  const groups = new Map<string, Permission[]>();

  for (const permission of permissions) {
    const resource = resourceOf(permission.name);

    groups.set(resource, [...(groups.get(resource) ?? []), permission]);
  }

  return (
    <form
      onSubmit={form.handleSubmit((values) => {
        setRefused([]);
        save.mutate(values);
      })}
      noValidate
      className="flex flex-col gap-4 rounded-xl border border-border p-4"
    >
      <h2 className="text-sm font-semibold">{role ? t("editTitle") : t("newTitle")}</h2>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-lg bg-destructive/10 px-3 py-2 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      <div className="flex max-w-md flex-col gap-1.5">
        <label htmlFor="roleName" className="text-sm font-medium">
          {t("name")}
        </label>
        <input
          {...form.register("name")}
          id="roleName"
          maxLength={100}
          aria-invalid={Boolean(errors.name)}
          aria-describedby={errors.name ? "roleName-error" : undefined}
          className={cn(CONTROL, errors.name && "border-destructive")}
        />
        {errors.name ? (
          <p id="roleName-error" className="text-xs text-destructive">
            {errors.name.message as string}
          </p>
        ) : null}
        {role?.isSystemTemplate ? (
          // Editable, and deliberately so: a tenant may recompose a template to fit how they work.
          // What they cannot do is delete it, because it is the way back to a working set (IAM-06).
          <p className="text-xs text-muted-foreground">{t("systemRoleHint")}</p>
        ) : null}
      </div>

      <fieldset
        className="flex flex-col gap-4"
        aria-describedby={errors.permissions ? "permissions-error" : undefined}
      >
        <legend className="text-sm font-medium">{t("permissions")}</legend>

        {errors.permissions ? (
          <p id="permissions-error" className="text-xs text-destructive">
            {errors.permissions.message as string}
          </p>
        ) : null}

        <div className="grid gap-4 sm:grid-cols-2">
          {[...groups].map(([resource, group]) => (
            <div key={resource} className="flex flex-col gap-2 rounded-lg border border-border p-3">
              <h3 className="font-mono text-xs font-semibold uppercase text-muted-foreground">
                {resource}
              </h3>

              {group.map((permission) => (
                <label key={permission.name} className="flex items-start gap-2 text-sm">
                  <input
                    type="checkbox"
                    value={permission.name}
                    {...form.register("permissions")}
                    className="mt-1 size-4 accent-primary"
                  />
                  <span>
                    {/* The description first, because it is the decision. The identifier is what
                        someone needs when reading a log or a spec, and second is where it belongs. */}
                    <span className="block">{permission.description}</span>
                    <span className="block font-mono text-xs text-muted-foreground">
                      {permission.name}
                    </span>
                  </span>
                </label>
              ))}
            </div>
          ))}
        </div>
      </fieldset>

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
