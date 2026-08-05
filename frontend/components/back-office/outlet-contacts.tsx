"use client";

import { useTranslations } from "next-intl";
import {
  useFieldArray,
  type Control,
  type FieldErrors,
  type FieldPath,
  type UseFormRegister,
} from "react-hook-form";

import { Button } from "@/components/ui/button";
import type { OutletContact } from "@/lib/api/outlets";
import { cn } from "@/lib/utils";

/**
 * The slice of the form this section owns.
 *
 * Narrower than the form's own shape on purpose: react-hook-form derives its field paths from the
 * type, and an array path over the `Record<string, unknown>` the rest of the form is typed as
 * resolves to `never` — there is no array in that type to index into. Naming the one branch this
 * component touches is what makes `contacts.1.email` a path TypeScript can check rather than a
 * string it has to be told to trust.
 */
type ContactsForm = { contacts: OutletContact[] };

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/**
 * The people at an outlet — store manager, buyer (`OUT-01`).
 *
 * **Personal data** ([B8](../../../docs/product/decisions-and-assumptions.md)). Removing a row and
 * saving deletes it outright rather than flagging it, which is what makes an erasure request a
 * thing an admin can simply do; the dedicated workflow is `OUT-10`.
 *
 * A list, so a field array rather than fixed controls — this is the one part of the form whose shape
 * comes from the data instead of the schema. Rows are keyed by react-hook-form's own `id` and never
 * by the index: an index key makes React reuse the input that held the deleted row's text for the
 * row that shifted up into it, so removing the first of three contacts appears to blank the wrong one.
 */
export function OutletContacts({
  control,
  register,
  errors,
}: {
  control: Control<ContactsForm>;
  register: UseFormRegister<ContactsForm>;
  errors: FieldErrors;
}) {
  const t = useTranslations("OutletForm");
  const { fields, append, remove } = useFieldArray({ control, name: "contacts" });

  const rows = errors.contacts as FieldErrors[] | undefined;

  return (
    <fieldset className="flex flex-col gap-3 rounded-xl border border-border p-4">
      <legend className="px-1 text-xs font-semibold text-muted-foreground uppercase">
        {t("contacts")}
      </legend>

      {fields.length === 0 ? <p className="text-sm text-muted-foreground">{t("noContacts")}</p> : null}

      {fields.map((row, index) => (
        <div key={row.id} className="grid gap-3 sm:grid-cols-[repeat(4,1fr)_auto] sm:items-start">
          <ContactField
            label={t("contactName")}
            name={`contacts.${index}.name`}
            required
            maxLength={200}
            register={register}
            error={rows?.[index]?.name?.message as string | undefined}
          />
          <ContactField
            label={t("contactRole")}
            name={`contacts.${index}.role`}
            maxLength={100}
            register={register}
            error={rows?.[index]?.role?.message as string | undefined}
          />
          <ContactField
            label={t("contactPhone")}
            name={`contacts.${index}.phone`}
            type="tel"
            maxLength={50}
            register={register}
            error={rows?.[index]?.phone?.message as string | undefined}
          />
          <ContactField
            label={t("contactEmail")}
            // `type="email"` and not `text`: it is the right keyboard on a phone, and the browser's
            // own check is a free first pass. The schema still owns the message, because the
            // browser's is untranslated and fires in a bubble the form cannot place.
            type="email"
            name={`contacts.${index}.email`}
            maxLength={320}
            register={register}
            error={rows?.[index]?.email?.message as string | undefined}
          />

          <Button
            type="button"
            variant="outline"
            className="sm:mt-6"
            onClick={() => remove(index)}
            // Numbered, so a screen reader moving button to button hears which row this removes
            // rather than "Remove" four times. The position and not the name: the name is whatever
            // was last rendered, which for a row someone just added is nothing at all.
            aria-label={t("removeContactNumbered", { number: index + 1 })}
          >
            {t("removeContact")}
          </Button>
        </div>
      ))}

      <div>
        <Button
          type="button"
          variant="outline"
          onClick={() => append({ name: "", role: null, phone: null, email: null })}
        >
          {t("addContact")}
        </Button>
      </div>
    </fieldset>
  );
}

function ContactField({
  label,
  name,
  type,
  required,
  maxLength,
  register,
  error,
}: {
  label: string;
  name: FieldPath<ContactsForm>;
  type?: "tel" | "email";
  required?: boolean;
  maxLength: number;
  register: UseFormRegister<ContactsForm>;
  error?: string;
}) {
  const errorId = `${name}-error`;

  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={name} className="text-sm font-medium">
        {label}
        {required ? (
          <span aria-hidden="true" className="ml-1 text-destructive">
            *
          </span>
        ) : null}
      </label>

      <input
        {...register(name)}
        id={name}
        type={type ?? "text"}
        maxLength={maxLength}
        aria-invalid={error !== undefined}
        aria-describedby={error ? errorId : undefined}
        className={cn(CONTROL, error && "border-destructive")}
      />

      {error ? (
        <p id={errorId} className="text-xs text-destructive">
          {error}
        </p>
      ) : null}
    </div>
  );
}

