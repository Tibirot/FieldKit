"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useState } from "react";
import { useForm, useWatch, type FieldErrors } from "react-hook-form";
import { z } from "zod";

import { useAuth } from "@/components/auth-provider";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import {
  createFieldDefinition,
  updateFieldDefinition,
  type CustomFieldType,
  type FieldDefinition,
} from "@/lib/api/field-definitions";
import { keyFromLabel } from "@/lib/forms/key-from-label";
import { useValidationMessages } from "@/lib/forms/use-validation-messages";
import { cn } from "@/lib/utils";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
  + " disabled:cursor-not-allowed disabled:opacity-60";

/** The five a tenant can describe with a rule the server can enforce (Configuration §6.1). */
const TYPES: readonly CustomFieldType[] = ["Text", "Number", "Boolean", "Date", "Choice"];

/** What the form holds. Numbers are strings while typing, so empty stays distinguishable from 0. */
type Values = {
  key: string;
  label: string;
  type: CustomFieldType;
  required: boolean;
  options: string;
  maxLength: string;
  minimum: string;
  maximum: string;
};

/**
 * Author one custom field (`CFG-01`).
 *
 * **The client validates presence and shape; the server owns everything else.** A choice with no
 * options and a minimum above its maximum are both refused by the API with the offending control
 * named — and its field keys (`key`, `label`, `options`, `maxLength`, `minimum`) are already this
 * form's field names, so a refusal lands beside the control that caused it without a translation
 * table. Re-stating those rules here would be a second declaration free to drift from the one that
 * actually decides, which is the trade `custom-fields.tsx` makes for values and this makes for
 * definitions.
 *
 * **Only the constraints the chosen type allows are sent.** The server clears options on a
 * non-choice itself, but not `maxLength` or the bounds — so a text field that was briefly a number
 * would keep bounds that render nowhere and validate nothing, and would become authoritative again
 * the moment someone switched it back.
 */
export function FieldDefinitionForm({
  definition,
  entity,
  onDone,
  onCancel,
}: {
  definition?: FieldDefinition;
  entity: FieldDefinition["entity"];
  onDone: () => void;
  onCancel: () => void;
}) {
  const t = useTranslations("CustomFields");
  const messages = useValidationMessages();
  const { user } = useAuth();
  const client = useQueryClient();

  const accessToken = user?.access_token;

  const form = useForm<Values>({
    resolver: zodResolver(
      z.object({
        key: z
          .string()
          .trim()
          .min(1, { message: messages.required })
          // Mirrors the API's own pattern. A format error is worth catching on blur rather than
          // after a round trip, and the server re-checks it either way.
          .regex(/^[a-z][a-z0-9_]{0,59}$/, { message: t("keyFormat") }),
        label: z.string().trim().min(1, { message: messages.required }).max(200, {
          message: messages.tooLong(200),
        }),
        type: z.enum(TYPES),
        required: z.boolean(),
        options: z.string(),
        maxLength: z.string(),
        minimum: z.string(),
        maximum: z.string(),
      }),
    ),
    mode: "onBlur",
    defaultValues: {
      key: definition?.key ?? "",
      label: definition?.label ?? "",
      type: definition?.type ?? "Text",
      required: definition?.required ?? false,
      options: definition?.options.join("\n") ?? "",
      maxLength: definition?.maxLength?.toString() ?? "",
      minimum: definition?.minimum?.toString() ?? "",
      maximum: definition?.maximum?.toString() ?? "",
    },
  });

  // Nothing renders differently once the key has been typed in, so a ref would be the obvious
  // shape — but writing one from a handler built during render is what the React Compiler's lint
  // refuses, and it is right to: the value would be read while rendering the very markup that
  // created the writer. State costs one re-render, once, since React bails out of a set to `true`
  // that is already `true`.
  const [keyEdited, setKeyEdited] = useState(false);

  const [refused, setRefused] = useState<readonly string[]>([]);

  // `useWatch` rather than `form.watch("type")`: the latter hands back a function, and the React
  // Compiler refuses to memoize any component that receives one — it would skip this whole form
  // rather than risk stale UI, and say so only as a lint warning (frontend-toolchain.md).
  const type = useWatch({ control: form.control, name: "type" });

  const save = useMutation({
    mutationFn: (values: Values) => {
      const write = {
        key: values.key.trim(),
        label: values.label.trim(),
        type: values.type,
        required: values.required,
        options:
          values.type === "Choice"
            ? values.options.split("\n").map((option) => option.trim()).filter(Boolean)
            : null,
        maxLength: values.type === "Text" ? number(values.maxLength) : null,
        minimum: values.type === "Number" ? number(values.minimum) : null,
        maximum: values.type === "Number" ? number(values.maximum) : null,
      };

      return definition
        ? updateFieldDefinition(accessToken!, definition.id, write)
        : createFieldDefinition(accessToken!, entity, write);
    },

    onSuccess: async () => {
      // The outlet form renders its custom section from this catalogue, so a definition changed here
      // changes a screen this form does not own.
      await client.invalidateQueries({ queryKey: ["field-definitions"] });
      onDone();
    },

    onError: (error) => {
      if (!(error instanceof ApiError)) {
        setRefused([t("saveFailed")]);
        return;
      }

      const unattributed: string[] = [];

      for (const problem of error.problems) {
        if (problem.field !== null && problem.field in form.getValues()) {
          form.setError(problem.field as keyof Values, { type: "server", message: problem.message });
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

  const labelField = form.register("label");

  return (
    <form
      onSubmit={form.handleSubmit((values) => {
        setRefused([]);
        save.mutate(values);
      })}
      noValidate
      className="flex flex-col gap-4 rounded-xl border border-border p-4"
    >
      <h2 className="text-sm font-semibold">{definition ? t("editTitle") : t("newTitle")}</h2>

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-lg bg-destructive/10 px-3 py-2 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
      ) : null}

      <div className="grid gap-4 sm:grid-cols-2">
        <Field id="fieldLabel" label={t("label")} hint={t("labelHint")} error={errors.label}>
          <input
            {...labelField}
            onChange={(event) => {
              labelField.onChange(event);

              // Only while creating, and only until someone types a key themselves: on an existing
              // definition the key is fixed, and overwriting a deliberate key would undo a choice.
              if (!definition && !keyEdited) {
                form.setValue("key", keyFromLabel(event.target.value));
              }
            }}
            id="fieldLabel"
            maxLength={200}
            aria-invalid={Boolean(errors.label)}
            aria-describedby={describedBy("fieldLabel", errors.label)}
            className={cn(CONTROL, errors.label && "border-destructive")}
          />
        </Field>

        <Field
          id="fieldKey"
          label={t("key")}
          hint={definition ? t("keyFixed") : t("keyHint")}
          error={errors.key}
        >
          <input
            {...form.register("key", { onChange: () => setKeyEdited(true) })}
            id="fieldKey"
            // Fixed after creation: it is the JSONB property name already written into every row,
            // and a rename would orphan every value stored under the old one. Disabled rather than
            // absent, so the field it describes is still visible while editing.
            disabled={Boolean(definition)}
            maxLength={60}
            spellCheck={false}
            aria-invalid={Boolean(errors.key)}
            aria-describedby={describedBy("fieldKey", errors.key)}
            className={cn(CONTROL, "font-mono", errors.key && "border-destructive")}
          />
        </Field>

        <Field id="fieldType" label={t("type")} hint={definition ? t("typeHint") : undefined} error={errors.type}>
          <select
            {...form.register("type")}
            id="fieldType"
            aria-invalid={Boolean(errors.type)}
            aria-describedby={describedBy("fieldType", errors.type)}
            className={cn(CONTROL, errors.type && "border-destructive")}
          >
            {TYPES.map((option) => (
              <option key={option} value={option}>
                {t(`types.${option}`)}
              </option>
            ))}
          </select>
        </Field>

        <div className="flex flex-col justify-center gap-1.5">
          <label htmlFor="fieldRequired" className="flex items-center gap-2 text-sm font-medium">
            <input
              {...form.register("required")}
              id="fieldRequired"
              type="checkbox"
              className="size-4 accent-primary"
            />
            {t("required")}
          </label>
          <p className="text-xs text-muted-foreground">{t("requiredHint")}</p>
        </div>
      </div>

      {/*
        Only the constraints the chosen type can carry. A maxLength beside a date is not a harmless
        extra control — it is a rule an admin would reasonably expect to apply, and nothing would
        ever enforce it.
      */}
      {type === "Choice" ? (
        <Field id="fieldOptions" label={t("options")} hint={t("optionsHint")} error={errors.options}>
          <textarea
            {...form.register("options")}
            id="fieldOptions"
            rows={4}
            spellCheck={false}
            aria-invalid={Boolean(errors.options)}
            aria-describedby={describedBy("fieldOptions", errors.options)}
            className={cn(
              CONTROL,
              "h-auto py-2 font-mono",
              errors.options && "border-destructive",
            )}
          />
        </Field>
      ) : null}

      {type === "Text" ? (
        <Field
          id="fieldMaxLength"
          label={t("maxLength")}
          hint={t("maxLengthHint")}
          error={errors.maxLength}
        >
          <input
            {...form.register("maxLength")}
            id="fieldMaxLength"
            type="number"
            min={1}
            step={1}
            aria-invalid={Boolean(errors.maxLength)}
            aria-describedby={describedBy("fieldMaxLength", errors.maxLength)}
            className={cn(CONTROL, "max-w-40", errors.maxLength && "border-destructive")}
          />
        </Field>
      ) : null}

      {type === "Number" ? (
        <div className="grid gap-4 sm:grid-cols-2">
          <Field id="fieldMinimum" label={t("minimum")} hint={t("boundsHint")} error={errors.minimum}>
            <input
              {...form.register("minimum")}
              id="fieldMinimum"
              type="number"
              step="any"
              aria-invalid={Boolean(errors.minimum)}
              aria-describedby={describedBy("fieldMinimum", errors.minimum)}
              className={cn(CONTROL, errors.minimum && "border-destructive")}
            />
          </Field>
          <Field id="fieldMaximum" label={t("maximum")} error={errors.maximum}>
            <input
              {...form.register("maximum")}
              id="fieldMaximum"
              type="number"
              step="any"
              aria-invalid={Boolean(errors.maximum)}
              aria-describedby={describedBy("fieldMaximum", errors.maximum)}
              className={cn(CONTROL, errors.maximum && "border-destructive")}
            />
          </Field>
        </div>
      ) : null}

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

/** An empty box means *no constraint*, which is not the same answer as zero. */
function number(raw: string): number | null {
  const trimmed = raw.trim();
  if (trimmed === "") return null;

  const parsed = Number(trimmed);

  // Passed through as null rather than NaN: `JSON.stringify(NaN)` is `null` anyway, and a bound the
  // browser could not parse is a bound nobody set.
  return Number.isFinite(parsed) ? parsed : null;
}

function describedBy(id: string, error: FieldErrors[string]): string | undefined {
  return error ? `${id}-error` : undefined;
}

function Field({
  id,
  label,
  hint,
  error,
  children,
}: {
  id: string;
  label: string;
  hint?: string;
  error: FieldErrors[string];
  children: React.ReactNode;
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-sm font-medium">
        {label}
      </label>
      {children}
      {error ? (
        <p id={`${id}-error`} className="text-xs text-destructive">
          {error.message as string}
        </p>
      ) : null}
      {hint ? <p className="text-xs text-muted-foreground">{hint}</p> : null}
    </div>
  );
}
