"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { UserForm } from "@/components/back-office/user-form";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import {
  fetchRoles,
  fetchUsers,
  rolesKey,
  setUserActive,
  usersKey,
  type Role,
  type User,
} from "@/lib/api/users";
import { cn } from "@/lib/utils";
import { usePermissions } from "@/lib/auth/use-permissions";

/**
 * The people who can sign in, and what they may do (`IAM-03`).
 *
 * Deactivated users stay on the list rather than disappearing. An account that is off is a fact an
 * admin needs to see — "why can't Ana log in" is answered here or nowhere — and hiding it would make
 * reactivation reachable only by someone who already knew to look.
 *
 * The **Device** column the wireframe draws is `IAM-07` and deferred: IAM has no device concept yet,
 * so a column here would be an empty promise ([UX build scope](../../../docs/ux/README.md)).
 */
export function UserBrowser() {
  const t = useTranslations("Users");
  const { user: signedIn } = useAuth();
  const client = useQueryClient();
  const { has } = usePermissions();

  const accessToken = signedIn?.access_token;
  const subject = signedIn?.profile.sub;

  const users = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: usersKey(subject ?? ""),
    queryFn: ({ signal }) => fetchUsers(accessToken!, signal),
  });

  const roles = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: rolesKey(subject ?? ""),
    queryFn: ({ signal }) => fetchRoles(accessToken!, signal),
  });

  const [editing, setEditing] = useState<User | "new" | null>(null);
  const [refused, setRefused] = useState<readonly string[]>([]);

  const setActive = useMutation({
    mutationFn: ({ user, active }: { user: User; active: boolean }) =>
      setUserActive(accessToken!, user.id, active),
    onSuccess: async () => {
      setRefused([]);
      await client.invalidateQueries({ queryKey: ["users"] });
    },
    onError: (error) => {
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? error.problems.map((problem) => problem.message)
          : [t("activationFailed")],
      );
    },
  });

  const byId = new Map((roles.data ?? []).map((role: Role) => [role.id, role]));
  const rows = users.data ?? [];

  return (
    <div className="flex flex-col gap-4">
      {has("user:write") ? (
        <div>
          <Button type="button" size="sm" onClick={() => setEditing("new")}>
            <Plus className="size-4" />
            {t("newUser")}
          </Button>
        </div>
      ) : null}

      {editing !== null ? (
        <UserForm
          // Remounted per target: react-hook-form captures its defaults on the first render.
          key={editing === "new" ? "new" : editing.id}
          user={editing === "new" ? undefined : editing}
          roles={roles.data ?? []}
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

      {users.isPending ? (
        <p className="text-sm text-muted-foreground">{t("loading")}</p>
      ) : users.isError ? (
        <p role="alert" className="text-sm text-destructive">
          {users.error instanceof ApiError && users.error.status === 403
            ? t("forbidden")
            : t("failed")}
        </p>
      ) : rows.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t("empty")}</p>
      ) : (
        <div className="overflow-x-auto rounded-xl border border-border">
          <table className="w-full text-sm">
            <caption className="sr-only">{t("caption")}</caption>
            <thead className="bg-muted/50 text-xs uppercase">
              <tr>
                <th scope="col" className="px-3 py-2 text-left font-medium">
                  {t("displayName")}
                </th>
                <th scope="col" className="px-3 py-2 text-left font-medium">
                  {t("email")}
                </th>
                <th scope="col" className="px-3 py-2 text-left font-medium">
                  {t("roles")}
                </th>
                <th scope="col" className="px-3 py-2 text-left font-medium">
                  {t("status")}
                </th>
                <th scope="col" className="px-3 py-2 text-right font-medium">
                  <span className="sr-only">{t("actions")}</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <tr key={row.id} className={cn("border-t border-border", !row.isActive && "opacity-60")}>
                  <th scope="row" className="px-3 py-2 text-left font-medium">
                    {row.displayName}
                  </th>
                  <td className="px-3 py-2 text-muted-foreground">{row.email}</td>
                  <td className="px-3 py-2">
                    {/* Named, not counted. "2 roles" is a number an admin then has to go and look
                        up, which is the click this column exists to save. */}
                    {row.roleIds.map((id) => byId.get(id)?.name ?? t("unknownRole")).join(", ")}
                  </td>
                  <td className="px-3 py-2">
                    <span
                      className={cn(
                        "rounded-full px-2 py-0.5 text-xs font-medium",
                        row.isActive
                          ? "bg-primary/15 text-primary"
                          : "bg-muted text-muted-foreground",
                      )}
                    >
                      {row.isActive ? t("active") : t("inactive")}
                    </span>
                  </td>
                  <td className="px-3 py-2 text-right">
                    <div className="flex justify-end gap-2">
                      {has("user:write") ? (
                        <>
                      <Button
                        type="button"
                        size="sm"
                        variant="outline"
                        onClick={() => setEditing(row)}
                        aria-label={t("editNamed", { name: row.displayName })}
                      >
                        {t("edit")}
                      </Button>
                      <Button
                        type="button"
                        size="sm"
                        variant="outline"
                        disabled={setActive.isPending}
                        onClick={() => setActive.mutate({ user: row, active: !row.isActive })}
                        aria-label={
                          row.isActive
                            ? t("deactivateNamed", { name: row.displayName })
                            : t("reactivateNamed", { name: row.displayName })
                        }
                      >
                        {row.isActive ? t("deactivate") : t("reactivate")}
                      </Button>
                        </>
                      ) : null}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
