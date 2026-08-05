"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { RoleForm } from "@/components/back-office/role-form";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import {
  deleteRole,
  fetchPermissions,
  fetchRoles,
  permissionsKey,
  resourceOf,
  rolesKey,
  type Role,
} from "@/lib/api/users";

/**
 * Roles, and the permissions each one bundles (`IAM-04`).
 *
 * A role is what an admin actually assigns, so this is where a permission becomes a decision rather
 * than a string. The catalogue it picks from is **code, not data** — a permission exists because
 * some module enforces it — which is why there is no way to invent one here.
 *
 * **A system template can be edited but not deleted.** It is the way back to a working set of roles
 * (`IAM-06`), so recomposing one is a tenant's business and stranding themselves with none is not.
 */
export function RoleBrowser() {
  const t = useTranslations("Roles");
  const { user } = useAuth();
  const client = useQueryClient();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const roles = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: rolesKey(subject ?? ""),
    queryFn: ({ signal }) => fetchRoles(accessToken!, signal),
  });

  const permissions = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: permissionsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchPermissions(accessToken!, signal),
  });

  const [editing, setEditing] = useState<Role | "new" | null>(null);
  const [refused, setRefused] = useState<readonly string[]>([]);

  const remove = useMutation({
    mutationFn: (role: Role) => deleteRole(accessToken!, role.id),
    onSuccess: async () => {
      setRefused([]);
      await client.invalidateQueries({ queryKey: ["roles"] });
    },
    onError: (error) => {
      // "4 user(s) still hold this role. Reassign them before deleting it." is a refusal an admin
      // can act on; "could not delete" is not.
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? error.problems.map((problem) => problem.message)
          : [t("deleteFailed")],
      );
    },
  });

  const rows = roles.data ?? [];

  return (
    <section className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-base font-semibold tracking-tight">{t("title")}</h2>
        <Button type="button" size="sm" onClick={() => setEditing("new")}>
          <Plus className="size-4" />
          {t("newRole")}
        </Button>
      </div>

      {editing !== null ? (
        <RoleForm
          // Remounted per target: react-hook-form captures its defaults on the first render.
          key={editing === "new" ? "new" : editing.id}
          role={editing === "new" ? undefined : editing}
          permissions={permissions.data ?? []}
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

      {roles.isPending ? (
        <p className="text-sm text-muted-foreground">{t("loading")}</p>
      ) : roles.isError ? (
        <p role="alert" className="text-sm text-destructive">
          {roles.error instanceof ApiError && roles.error.status === 403
            ? t("forbidden")
            : t("failed")}
        </p>
      ) : (
        <ul className="flex flex-col gap-3">
          {rows.map((role) => (
            <li
              key={role.id}
              className="flex flex-col gap-2 rounded-xl border border-border p-4 sm:flex-row sm:items-start sm:justify-between"
            >
              <div className="flex flex-col gap-1.5">
                <div className="flex items-center gap-2">
                  <span className="text-sm font-medium">{role.name}</span>
                  {role.isSystemTemplate ? (
                    <span className="rounded-full bg-muted px-2 py-0.5 text-xs text-muted-foreground">
                      {t("systemRole")}
                    </span>
                  ) : null}
                </div>

                {role.permissions.length === 0 ? (
                  // A role that grants nothing is a real state, not a broken one — a group named
                  // before anyone decided what it may do. Saying so beats an empty space.
                  <p className="text-xs text-muted-foreground">{t("grantsNothing")}</p>
                ) : (
                  <ul className="flex flex-wrap gap-1.5">
                    {/* The permissions themselves, not a count. Which ones a role carries is the
                        question this list is asked, and a number sends someone into the form to
                        find out. Grouped by resource, so `outlet:read` and `outlet:write` read as
                        one decision about outlets. */}
                    {[...new Set(role.permissions.map(resourceOf))].sort().map((resource) => (
                      <span
                        key={resource}
                        className="rounded-md bg-muted px-2 py-0.5 font-mono text-xs"
                        title={role.permissions.filter((p) => resourceOf(p) === resource).join(", ")}
                      >
                        {resource}
                        <span className="ml-1 text-muted-foreground">
                          {role.permissions
                            .filter((p) => resourceOf(p) === resource)
                            .map((p) => p.slice(resource.length + 1))
                            .join(" · ")}
                        </span>
                      </span>
                    ))}
                  </ul>
                )}
              </div>

              <div className="flex shrink-0 gap-2">
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  onClick={() => setEditing(role)}
                  aria-label={t("editNamed", { name: role.name })}
                >
                  {t("edit")}
                </Button>
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  // Not hidden on a system template: the refusal explains what a template is for,
                  // which is worth more than a button that quietly is not there.
                  disabled={remove.isPending}
                  onClick={() => remove.mutate(role)}
                  aria-label={t("deleteNamed", { name: role.name })}
                >
                  {t("delete")}
                </Button>
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
