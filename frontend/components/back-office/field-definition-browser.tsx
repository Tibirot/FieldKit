"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Plus } from "lucide-react";
import { useTranslations } from "next-intl";
import { useState } from "react";

import { useAuth } from "@/components/auth-provider";
import { FieldDefinitionForm } from "@/components/back-office/field-definition-form";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import {
  deleteFieldDefinition,
  fetchFieldDefinitions,
  fieldDefinitionsKey,
  type FieldDefinition,
} from "@/lib/api/field-definitions";
import { usePermissions } from "@/lib/auth/use-permissions";

/**
 * The custom fields a tenant has declared for one entity (`CFG-01`).
 *
 * **This is the config-driven story's missing half.** The outlet form has rendered a tenant's own
 * fields from this catalogue since Week 4, and the import validates against it — but nothing could
 * put anything *in* it. Every definition in the running system existed because an integration test
 * had created one, which meant a second tenant got a product advertised as customizable with no way
 * to customize it. The Phase 1 review found it the same way the channel screen was found: by asking
 * what an admin would press.
 *
 * **One entity per screen, passed in.** The catalogue is per-entity and outlets are the only entity
 * with custom fields wired through today; products arrive in W6 and bring their own screen rather
 * than an entity dropdown here that would list three destinations that do not exist.
 */
export function FieldDefinitionBrowser({ entity }: { entity: FieldDefinition["entity"] }) {
  const t = useTranslations("CustomFields");
  const { user } = useAuth();
  const client = useQueryClient();
  const { has } = usePermissions();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;

  const definitions = useQuery({
    enabled: Boolean(accessToken && subject),
    queryKey: fieldDefinitionsKey(subject ?? "", entity),
    queryFn: ({ signal }) => fetchFieldDefinitions(accessToken!, entity, signal),
  });

  const [editing, setEditing] = useState<FieldDefinition | "new" | null>(null);
  const [confirming, setConfirming] = useState<string | null>(null);
  const [refused, setRefused] = useState<readonly string[]>([]);

  const remove = useMutation({
    mutationFn: (definition: FieldDefinition) =>
      deleteFieldDefinition(accessToken!, definition.id),
    onSuccess: async () => {
      setRefused([]);
      setConfirming(null);
      await client.invalidateQueries({ queryKey: ["field-definitions"] });
    },
    onError: (error) => {
      setRefused(
        error instanceof ApiError && error.problems.length > 0
          ? error.problems.map((problem) => problem.message)
          : [t("deleteFailed")],
      );
    },
  });

  const rows = definitions.data ?? [];
  const writable = has("config:write");

  return (
    <div className="flex flex-col gap-4">
      {writable ? (
        <div>
          <Button type="button" size="sm" onClick={() => setEditing("new")}>
            <Plus className="size-4" />
            {t("newField")}
          </Button>
        </div>
      ) : null}

      {editing !== null ? (
        <FieldDefinitionForm
          // Remounted per target: react-hook-form captures its defaults on the first render.
          key={editing === "new" ? "new" : editing.id}
          definition={editing === "new" ? undefined : editing}
          entity={entity}
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

      {definitions.isPending ? (
        <p className="text-sm text-muted-foreground">{t("loading")}</p>
      ) : definitions.isError ? (
        <p role="alert" className="text-sm text-destructive">
          {definitions.error instanceof ApiError && definitions.error.status === 403
            ? t("forbidden")
            : t("failed")}
        </p>
      ) : rows.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t("empty")}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-border rounded-xl border border-border">
          {rows.map((definition) => (
            <li key={definition.id} className="flex flex-col gap-2 px-4 py-3 text-sm">
              <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
                <span className="font-medium">{definition.label}</span>
                <code className="font-mono text-xs text-muted-foreground">{definition.key}</code>
                <span className="text-xs text-muted-foreground">
                  {t(`types.${definition.type}`)}
                  {definition.required ? ` · ${t("required")}` : ""}
                  {constraint(definition) ? ` · ${constraint(definition)}` : ""}
                </span>

                {writable ? (
                  <div className="ml-auto flex gap-2">
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      onClick={() => setEditing(definition)}
                      aria-label={t("editNamed", { label: definition.label })}
                    >
                      {t("edit")}
                    </Button>
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      onClick={() => setConfirming(definition.id)}
                      aria-label={t("deleteNamed", { label: definition.label })}
                    >
                      {t("delete")}
                    </Button>
                  </div>
                ) : null}
              </div>

              {/*
                Confirmed, unlike deleting a channel — and for a reason worth stating rather than a
                general nervousness about delete buttons. A channel still in use is refused by the
                API with a count; this cannot be, because Configuration does not own the rows that
                hold the values (ADR-0005). The values stay in each outlet's JSONB, undescribed,
                until that outlet is next saved — and then they are gone. Nothing else on this
                screen would ever mention them.
              */}
              {confirming === definition.id ? (
                <div
                  role="alert"
                  className="flex flex-col gap-2 rounded-lg bg-muted px-3 py-2 text-xs"
                >
                  <p>{t("deleteWarning", { key: definition.key })}</p>
                  <div className="flex gap-2">
                    <Button
                      type="button"
                      size="sm"
                      variant="destructive"
                      disabled={remove.isPending}
                      onClick={() => remove.mutate(definition)}
                    >
                      {t("confirmDelete")}
                    </Button>
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      onClick={() => setConfirming(null)}
                    >
                      {t("cancel")}
                    </Button>
                  </div>
                </div>
              ) : null}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/**
 * The rule this definition carries, in the shortest form that still says which rule it is.
 *
 * Only the one its type allows, because that is the only one stored — see the form for why a bound
 * is never kept on a field that is not a number.
 */
function constraint(definition: FieldDefinition): string | null {
  if (definition.type === "Choice") return definition.options.join(", ") || null;
  if (definition.type === "Text") return definition.maxLength ? `≤ ${definition.maxLength}` : null;

  if (definition.type === "Number") {
    const { minimum: min, maximum: max } = definition;
    if (min !== null && max !== null) return `${min} – ${max}`;
    if (min !== null) return `≥ ${min}`;
    if (max !== null) return `≤ ${max}`;
  }

  return null;
}
