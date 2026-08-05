"use client";

import { useTranslations } from "next-intl";
import { Controller, type Control, type FieldErrors } from "react-hook-form";

import type { FieldDefinition } from "@/lib/api/field-definitions";
import { cn } from "@/lib/utils";

/**
 * A tenant's own fields, rendered from the catalogue it declared (`CFG-01`, `CFG-02`).
 *
 * The config-driven story with nothing between the declaration and the screen: no code here knows
 * what a chiller count is, or that this tenant has one.
 *
 * **The rules come from the same descriptor, through `customFieldSchema`.** That is the discipline
 * worth keeping — the server owns these rules (`BR-CFG-3` re-validates every write), so anything
 * hand-written here would be a second declaration free to drift. Derived, it cannot.
 *
 * The controls still carry the native constraints too. They are free, they let the browser refuse
 * before a keystroke reaches React, and for `number` and `date` they are what produces the right
 * keyboard on a phone. What they could never do is put a message beside the control that caused it,
 * which is what the schema is for.
 */
export function CustomFields({
  definitions,
  control,
  errors,
}: {
  definitions: FieldDefinition[];
  control: Control<Record<string, unknown>>;
  errors: FieldErrors;
}) {
  const t = useTranslations("OutletForm");

  if (definitions.length === 0) return null;

  return (
    <fieldset className="flex flex-col gap-3 rounded-xl border border-border p-4">
      <legend className="px-1 text-xs font-semibold text-muted-foreground uppercase">
        {t("customFields")}
      </legend>

      {definitions.map((definition) => (
        <Controller
          key={definition.key}
          name={`custom.${definition.key}`}
          control={control}
          render={({ field }) => (
            <CustomField
              definition={definition}
              value={field.value}
              onChange={field.onChange}
              onBlur={field.onBlur}
              error={
                (errors.custom as FieldErrors | undefined)?.[definition.key]?.message as
                  | string
                  | undefined
              }
            />
          )}
        />
      ))}
    </fieldset>
  );
}

const CONTROL =
  "h-9 rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

function CustomField({
  definition,
  value,
  onChange,
  onBlur,
  error,
}: {
  definition: FieldDefinition;
  value: unknown;
  onChange: (value: unknown) => void;
  onBlur: () => void;
  error?: string;
}) {
  const id = `custom-${definition.key}`;
  const errorId = `${id}-error`;

  const shared = {
    id,
    onBlur,
    // Pointed at the message rather than repeating it: a screen reader reads the described-by text,
    // so putting it in an aria-label as well would announce it twice.
    "aria-invalid": error !== undefined,
    "aria-describedby": error ? errorId : undefined,
  };

  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-sm font-medium">
        {definition.label}
        {/*
          Decorative — `required` on the control is what a screen reader announces and what the
          browser enforces. Marking it twice reads as "required required".
        */}
        {definition.required ? (
          <span aria-hidden="true" className="ml-1 text-destructive">
            *
          </span>
        ) : null}
      </label>

      {definition.type === "Boolean" ? (
        <input
          {...shared}
          type="checkbox"
          // Never `required`: on a checkbox that means "must be ticked", while the catalogue means
          // "must have an answer" — and no is an answer. The schema encodes the same distinction.
          checked={value === true}
          onChange={(event) => onChange(event.target.checked)}
          className="size-4 accent-primary"
        />
      ) : definition.type === "Choice" ? (
        <select
          {...shared}
          required={definition.required}
          value={typeof value === "string" ? value : ""}
          onChange={(event) => onChange(event.target.value || null)}
          className={cn(CONTROL, error && "border-destructive")}
        >
          <option value="" />
          {definition.options.map((option) => (
            <option key={option} value={option}>
              {option}
            </option>
          ))}
        </select>
      ) : (
        <input
          {...shared}
          required={definition.required}
          type={definition.type === "Number" ? "number" : definition.type === "Date" ? "date" : "text"}
          maxLength={definition.maxLength ?? undefined}
          min={definition.minimum ?? undefined}
          max={definition.maximum ?? undefined}
          // Any decimal: a definition says nothing about precision, and the default step of 1 would
          // silently refuse 12.5 for a field whose bounds allow it.
          step={definition.type === "Number" ? "any" : undefined}
          value={value === undefined || value === null ? "" : String(value)}
          onChange={(event) => onChange(read(definition, event.target.value))}
          className={cn(CONTROL, error && "border-destructive")}
        />
      )}

      {error ? (
        <p id={errorId} className="text-xs text-destructive">
          {error}
        </p>
      ) : null}
    </div>
  );
}

/**
 * Turns what the control holds into what the schema and the API expect.
 *
 * An empty control means *absent*, not empty-string — the same rule the CSV import follows, and for
 * the same reason: an optional choice left alone must not arrive as `""` and fail as "not one of the
 * options". A number becomes a number here rather than on the server, because unlike a CSV this
 * client knows the type it is holding.
 *
 * **`null`, not `undefined`.** React Hook Form cannot tell `onChange(undefined)` from a handler
 * invoked with no argument, so clearing a field silently kept its old value — found by a test that
 * emptied a number and watched 4 survive. The API already treats a JSON null as absent
 * (`CustomFieldValidator` checks `JsonValueKind.Null` alongside a missing key), so this is the same
 * meaning in a value RHF can carry.
 */
function read(definition: FieldDefinition, raw: string): unknown {
  if (raw === "") return null;
  if (definition.type !== "Number") return raw;

  const parsed = Number(raw);

  // Not-a-number is passed through as the text it was, so the schema's message names the type
  // mismatch rather than this silently producing `NaN`.
  return Number.isFinite(parsed) ? parsed : raw;
}
