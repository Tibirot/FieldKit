"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { AssignmentForm } from "@/components/back-office/assignment-form";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import {
  assignmentsKey,
  deleteAssignment,
  fetchAssignments,
  type RepAssignment,
  type Territory,
} from "@/lib/api/org";
import { fetchUsers, usersKey } from "@/lib/api/users";
import { useBusinessDay } from "@/lib/dates";
import { cn } from "@/lib/utils";

/**
 * Who covers a territory, and when (`ORG-04`).
 *
 * A history rather than a current holder: an assignment has a period, so the panel shows the ones
 * that have ended and the ones that have not started as well as today's. **`BR-ORG-2` allows exactly
 * one rep at a time**, which the server enforces — so more than one row here means they do not
 * overlap, not that the rule was bent.
 *
 * `isCurrent` is the server's answer, resolved in the calling user's timezone. Deciding it here would
 * use the browser's, and the two disagree for anyone travelling — which is most of a sales
 * organisation.
 */
export function TerritoryAssignments({ territory }: { territory: Territory }) {
  const t = useTranslations("Territories");
  const day = useBusinessDay();
  const { user } = useAuth();
  const client = useQueryClient();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const assignments = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: assignmentsKey(subject ?? "", territory.id),
    queryFn: ({ signal }) => fetchAssignments(accessToken!, territory.id, signal),
  });

  const users = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: usersKey(subject ?? ""),
    queryFn: ({ signal }) => fetchUsers(accessToken!, signal),
  });

  const [editing, setEditing] = useState<RepAssignment | "new" | null>(null);
  const [refused, setRefused] = useState<readonly string[]>([]);

  const remove = useMutation({
    mutationFn: (assignment: RepAssignment) => deleteAssignment(accessToken!, assignment.id),
    onSuccess: async () => {
      setRefused([]);
      await client.invalidateQueries({ queryKey: ["assignments"] });
    },
    onError: (error) => {
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? error.problems.map((problem) => problem.message)
          : [t("removeFailed")],
      );
    },
  });

  // Deactivated users are filtered out of the picker rather than shown and refused: the server
  // rejects assigning one, and offering the choice only to take it back is worse than not offering
  // it. Existing assignments to a since-deactivated rep still render — their history stands.
  const assignable = (users.data ?? []).filter((candidate) => candidate.isActive);

  const rows = assignments.data ?? [];

  return (
    <section className="flex flex-col gap-3 rounded-xl border border-border p-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="text-sm font-semibold">{t("assignmentsFor", { name: territory.name })}</h2>
        <Button type="button" size="sm" onClick={() => setEditing("new")}>
          <Plus className="size-4" />
          {t("assignRep")}
        </Button>
      </div>

      {editing !== null ? (
        <AssignmentForm
          // Remounted per target: react-hook-form captures its defaults on the first render, so
          // without this, editing a second assignment would show the first one's dates.
          key={editing === "new" ? "new" : editing.id}
          territoryId={territory.id}
          assignment={editing === "new" ? undefined : editing}
          users={assignable}
          onDone={() => setEditing(null)}
          onCancel={() => setEditing(null)}
        />
      ) : null}

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-lg bg-destructive/10 px-3 py-2 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      {assignments.isPending ? (
        <p className="text-sm text-muted-foreground">{t("loadingAssignments")}</p>
      ) : assignments.isError ? (
        <p role="alert" className="text-sm text-destructive">
          {t("assignmentsFailed")}
        </p>
      ) : rows.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t("noAssignments")}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-border">
          {rows.map((assignment) => (
            <li key={assignment.id} className="flex flex-wrap items-center gap-3 py-2 text-sm">
              <span className="font-medium">
                {/* Null when the directory no longer resolves the subject. The assignment still
                    stands, and saying so is better than rendering an empty cell. */}
                {assignment.displayName ?? t("unknownRep")}
              </span>

              <span className={cn("text-muted-foreground", assignment.isCurrent && "text-foreground")}>
                {assignment.to
                  ? t("period", { from: day(assignment.from), to: day(assignment.to) })
                  : t("periodOpen", { from: day(assignment.from) })}
              </span>

              {assignment.isCurrent ? (
                <span className="rounded-full bg-primary/15 px-2 py-0.5 text-xs font-medium text-primary">
                  {t("current")}
                </span>
              ) : null}

              <div className="ml-auto flex gap-2">
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  onClick={() => setEditing(assignment)}
                  aria-label={t("editAssignmentNamed", {
                    name: assignment.displayName ?? t("unknownRep"),
                  })}
                >
                  {t("edit")}
                </Button>
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  disabled={remove.isPending}
                  onClick={() => remove.mutate(assignment)}
                  aria-label={t("removeAssignmentNamed", {
                    name: assignment.displayName ?? t("unknownRep"),
                  })}
                >
                  {t("remove")}
                </Button>
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
