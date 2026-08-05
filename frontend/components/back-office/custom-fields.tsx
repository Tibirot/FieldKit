"use client";

import { useTranslations } from "next-intl";

import type { FieldDefinition } from "@/lib/api/field-definitions";

/**
 * A tenant's own fields, rendered from the catalogue it declared (`CFG-01`, `CFG-02`).
 *
 * This is the config-driven story with nothing between the declaration and the screen: no code here
 * knows what a chiller count is, or that this tenant has one.
 *
 * **Validated by the browser, from the same definition.** Every constraint a field definition can
 * express maps onto a native HTML attribute — `maxLength` to `maxlength`, a number's bounds to
 * `min`/`max`, a choice to a `<select>`, `required` to `required`. So the browser enforces them,
 * for free, accessibly, before a request is made.
 *
 * That is deliberate rather than lazy. `BR-CFG-3` already says the server validates authoritatively
 * and the client only mirrors it for UX — so the client's job is to be *fast and cheap*, not to be a
 * second source of truth. A form library plus a runtime-built schema would be a second
 * implementation of rules the server already owns, and one that can disagree with them.
 */
export function CustomFields({
  definitions,
  values,
  onChange,
}: {
  definitions: FieldDefinition[];
  values: Record<string, unknown>;
  onChange: (key: string, value: unknown) => void;
}) {
  const t = useTranslations("OutletForm");

  if (definitions.length === 0) return null;

  return (
    <fieldset className="flex flex-col gap-3 rounded-xl border border-border p-4">
      <legend className="px-1 text-xs font-semibold text-muted-foreground uppercase">
        {t("customFields")}
      </legend>

      {definitions.map((definition) => (
        <CustomField
          key={definition.key}
          definition={definition}
          value={values[definition.key]}
          onChange={(value) => onChange(definition.key, value)}
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
}: {
  definition: FieldDefinition;
  value: unknown;
  onChange: (value: unknown) => void;
}) {
  const id = `custom-${definition.key}`;
  const shared = { id, name: definition.key, required: definition.required };

  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={id} className="text-sm font-medium">
        {definition.label}
        {/*
          The asterisk is decorative — `required` on the control is what a screen reader announces,
          and what the browser enforces. Marking it twice in the accessibility tree would have it
          read out as "required required".
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
          // A checkbox cannot be `required` and mean "either answer is fine" — required on a
          // checkbox means "must be ticked", which is a different rule than the catalogue's.
          required={false}
          checked={value === true}
          onChange={(event) => onChange(event.target.checked)}
          className="size-4 accent-primary"
        />
      ) : definition.type === "Choice" ? (
        <select
          {...shared}
          value={typeof value === "string" ? value : ""}
          onChange={(event) => onChange(event.target.value || undefined)}
          className={CONTROL}
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
          type={definition.type === "Number" ? "number" : definition.type === "Date" ? "date" : "text"}
          // Each of these is the catalogue's own constraint, handed to the browser verbatim.
          maxLength={definition.maxLength ?? undefined}
          min={definition.minimum ?? undefined}
          max={definition.maximum ?? undefined}
          // Any decimal, because a definition says nothing about precision and the default of "1"
          // would silently refuse 12.5 for a field whose bounds allow it.
          step={definition.type === "Number" ? "any" : undefined}
          value={value === undefined || value === null ? "" : String(value)}
          onChange={(event) => onChange(read(definition, event.target.value))}
          className={CONTROL}
        />
      )}
    </div>
  );
}

/**
 * Turns what the input holds into what the API expects.
 *
 * An empty control means *absent*, not empty-string — the same rule the CSV import follows, and for
 * the same reason: an optional choice left alone must not arrive as `""` and fail as "not one of the
 * options". A number becomes a number here rather than on the server, because unlike a CSV this
 * client knows the type it is holding.
 */
function read(definition: FieldDefinition, raw: string): unknown {
  if (raw === "") return undefined;
  if (definition.type !== "Number") return raw;

  const parsed = Number(raw);

  // Not-a-number is passed through as the text it was, so the server's message names the field
  // rather than this silently sending `NaN` and getting a refusal about the wrong thing.
  return Number.isFinite(parsed) ? parsed : raw;
}
