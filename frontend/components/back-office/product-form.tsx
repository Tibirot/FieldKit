"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { useMemo, useState } from "react";
import { useForm, type FieldPath as RhfFieldPath } from "react-hook-form";
import { z } from "zod";

import { useAuth } from "@/components/auth-provider";
import { CustomFields } from "@/components/back-office/custom-fields";
import { Button } from "@/components/ui/button";
import { ApiError } from "@/lib/api/client";
import { refusalText } from "@/lib/api/refusals";
import { fetchFieldDefinitions, fieldDefinitionsKey } from "@/lib/api/field-definitions";
import {
  brandsKey,
  categoriesKey,
  createProduct,
  fetchBrands,
  fetchCategories,
  fetchTaxClasses,
  taxClassesKey,
  updateProduct,
  withAncestry,
  type CreateProduct,
  type Product,
  type ProductWrite,
} from "@/lib/api/products";
import { customFieldSchema, type ValidationMessages } from "@/lib/forms/custom-field-schema";
import { useValidationMessages } from "@/lib/forms/use-validation-messages";

const CONTROL =
  "h-9 w-full rounded-lg border border-input bg-background px-3 text-sm text-foreground"
  + " focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none";

/** How the API nests custom fields — the one place its naming and this form's disagree. */
const CustomFieldPrefix = "customFields.";

type FieldPath = RhfFieldPath<Record<string, unknown>>;

/** Trimmed, and empty becomes absent — the shape every optional field on the API expects. */
const optionalText = z
  .string()
  .trim()
  .transform((value) => (value === "" ? null : value))
  .nullable();

/**
 * The fields every product has, whatever a tenant added to them.
 *
 * The lengths and bounds match the API's own — a SKU one character too long should be a message
 * under that field rather than a round trip that comes back refused.
 */
function fixedSchema(m: ValidationMessages) {
  const text = (max: number) =>
    z.string().trim().min(1, { message: m.required }).max(max, { message: m.tooLong(max) });

  return z.object({
    sku: text(64),
    name: text(200),

    // The three classifications are optional (`PRD-01`), so "" is a real choice rather than a
    // missing answer — see the note on the selects.
    brandId: optionalText,
    categoryId: optionalText,
    taxClassId: optionalText,

    unitOfMeasure: optionalText,

    // Text in, number out. An emptied number input holds "", and z.number() would reject that as
    // "expected number" for a field nobody filled in on purpose. Below 1 is refused here because
    // the API refuses it too: a pack of zero is a typo, not "no pack size".
    packSize: z
      .string()
      .trim()
      .transform((value) => (value === "" ? null : Number(value)))
      .refine((value) => value === null || (Number.isInteger(value) && value >= 1), {
        message: m.atLeast(1),
      }),

    status: z.enum(["Active", "Discontinued"]),
  });
}

function Field({
  label,
  htmlFor,
  required,
  hint,
  error,
  children,
}: {
  label: string;
  htmlFor: string;
  required?: boolean;
  hint?: string;
  error?: string;
  children: React.ReactNode;
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={htmlFor} className="text-sm font-medium">
        {label}
        {required ? (
          <span aria-hidden="true" className="ml-1 text-destructive">
            *
          </span>
        ) : null}
      </label>
      {children}
      {hint ? <p className="text-xs text-muted-foreground">{hint}</p> : null}
      {error ? (
        <p id={`${htmlFor}-error`} className="text-xs text-destructive">
          {error}
        </p>
      ) : null}
    </div>
  );
}

/**
 * Create or edit a product (`PRD-01`).
 *
 * One component for both, because they are the same form with one field's worth of difference: a
 * **SKU is set at creation and never again**. That is the API's rule and it is not arbitrary —
 * changing a SKU makes this a different product, and every order line already pointing at this id
 * would then describe something else.
 *
 * **Status *is* here, unlike on the outlet form.** Closing a shop is a one-way door with its own
 * endpoint (`OUT-04`), so putting it in a form would let a careless edit close it while fixing a
 * typo. Discontinuing a product is not one-way — seasonal lines come back — so it is an ordinary
 * field, and the API models it as one.
 */
export function ProductForm({
  product,
  onDone,
  onCancel,
}: {
  product?: Product;
  onDone: () => void;
  onCancel: () => void;
}) {
  const t = useTranslations("Products");

  // Server refusals, in the reader's language (ADR-0012 stage 2).

  const refusals = useTranslations("Refusals");
  const client = useQueryClient();
  const { user } = useAuth();

  const accessToken = user?.access_token;
  const subject = user?.profile.sub;
  const enabled = Boolean(accessToken && subject);

  const brands = useQuery({
    enabled,
    queryKey: brandsKey(subject ?? ""),
    queryFn: ({ signal }) => fetchBrands(accessToken!, signal),
  });

  const taxClasses = useQuery({
    enabled,
    queryKey: taxClassesKey(subject ?? ""),
    queryFn: ({ signal }) => fetchTaxClasses(accessToken!, signal),
  });

  const categories = useQuery({
    enabled,
    queryKey: categoriesKey(subject ?? ""),
    queryFn: ({ signal }) => fetchCategories(accessToken!, signal),
  });

  const definitions = useQuery({
    enabled,
    queryKey: fieldDefinitionsKey(subject ?? "", "Product"),
    queryFn: ({ signal }) => fetchFieldDefinitions(accessToken!, "Product", signal),
  });

  const messages = useValidationMessages();

  // Rebuilt when the catalogue arrives, and only then. The resolver closes over this, so a schema
  // recreated every render would revalidate on every keystroke against a brand-new object.
  const schema = useMemo(
    () =>
      fixedSchema(messages).extend({
        custom: customFieldSchema(definitions.data ?? [], messages),
      }),
    [definitions.data, messages],
  );

  const form = useForm({
    resolver: zodResolver(schema),

    // On blur rather than on change, matching the outlet form: telling someone their SKU is too
    // long while they type the second character is noise.
    mode: "onBlur",

    defaultValues: {
      sku: product?.sku ?? "",
      name: product?.name ?? "",
      brandId: product?.brandId ?? "",
      categoryId: product?.categoryId ?? "",
      taxClassId: product?.taxClassId ?? "",
      unitOfMeasure: product?.unitOfMeasure ?? "",
      packSize: product?.packSize?.toString() ?? "",
      status: product?.status ?? "Active",
      custom: product?.customFields ?? {},
    },
  });

  const [refused, setRefused] = useState<readonly string[]>([]);

  const save = useMutation({
    mutationFn: (body: CreateProduct | ProductWrite) =>
      product
        ? updateProduct(accessToken!, product.id, body)
        : createProduct(accessToken!, body as CreateProduct),

    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ["products"] });
      onDone();
    },

    onError: (error) => {
      if (!(error instanceof ApiError)) {
        setRefused([t("saveFailed")]);
        return;
      }

      // A problem the API attached to a field goes under that control, exactly like a client-side
      // one. Anything it could not attribute stays at the top, because a message pinned to a
      // guessed control is worse than one that admits it is about the request.
      const unattributed: string[] = [];

      for (const problem of error.problems) {
        const path = formPath(problem.field);

        if (path) form.setError(path as never, { type: "server", message: refusalText(refusals, problem) });
        else unattributed.push(refusalText(refusals, problem));
      }

      // A refusal the API attached to nothing — a 403, a 404, a 500 with no body — still has to say
      // something. Without this the loop above runs zero times and the screen goes silent, which reads
      // as a Save button that does nothing rather than as a refusal.
      setRefused(error.problems.length > 0 ? unattributed : [t("saveFailed")]);
    },
  });

  /**
   * The API's field path, as this form names it, or undefined if it renders no such control.
   *
   * The two agree everywhere except custom fields, which the request nests under `customFields`
   * while the form holds them under `custom`. Checked against what is actually on screen, because
   * `setError` on a path with no control swallows the message silently — and an unknown field is a
   * rule the API grew rather than a reason to lose what it said.
   */
  function formPath(field: string | null): FieldPath | undefined {
    if (!field) return undefined;

    if (field.startsWith(CustomFieldPrefix)) {
      const key = field.slice(CustomFieldPrefix.length);

      return (definitions.data ?? []).some((definition) => definition.key === key)
        ? (`custom.${key}` as FieldPath)
        : undefined;
    }

    return field in form.getValues() ? (field as FieldPath) : undefined;
  }

  const errors = form.formState.errors;
  const categoryOptions = useMemo(() => withAncestry(categories.data ?? []), [categories.data]);

  const onSubmit = form.handleSubmit((values) => {
    setRefused([]);

    // `custom` becomes `customFields` here and nowhere else — one rename in one place, rather than a
    // form whose control names are chosen by the wire format.
    const body: ProductWrite = {
      name: values.name,
      brandId: values.brandId,
      categoryId: values.categoryId,
      taxClassId: values.taxClassId,
      unitOfMeasure: values.unitOfMeasure,
      packSize: values.packSize,
      status: values.status,
      customFields: values.custom as Record<string, unknown>,
    };

    // The SKU is only ever sent on create — see the disabled control above.
    save.mutate(product ? body : { ...body, sku: values.sku });
  });

  /** A vocabulary that has not been authored yet, said out loud rather than left as an empty list. */
  const emptyHint = (query: { isPending: boolean; data?: unknown[] }) =>
    !query.isPending && (query.data?.length ?? 0) === 0 ? t("vocabularyEmpty") : undefined;

  return (
    <form
      onSubmit={onSubmit}
      noValidate
      className="flex flex-col gap-4 rounded-xl border border-border p-4"
    >
      <div className="grid gap-4 sm:grid-cols-2">
        <Field
          label={t("sku")}
          htmlFor="sku"
          required
          hint={product ? t("skuFixed") : t("skuHint")}
          error={errors.sku?.message}
        >
          <input
            id="sku"
            className={CONTROL}
            // A SKU is the identifier order lines already point at. The API has no parameter for
            // changing it, so the control says the same thing rather than accepting an edit the
            // request would silently drop.
            disabled={Boolean(product)}
            aria-invalid={Boolean(errors.sku)}
            aria-describedby={errors.sku ? "sku-error" : undefined}
            {...form.register("sku")}
          />
        </Field>

        <Field label={t("name")} htmlFor="name" required error={errors.name?.message}>
          <input
            id="name"
            className={CONTROL}
            aria-invalid={Boolean(errors.name)}
            aria-describedby={errors.name ? "name-error" : undefined}
            {...form.register("name")}
          />
        </Field>

        <Field
          label={t("brand")}
          htmlFor="brandId"
          hint={emptyHint(brands)}
          error={errors.brandId?.message}
        >
          {/* Optional, so the blank option is a real answer rather than a prompt — which is why it
              reads "No brand" instead of "Select a brand". */}
          <select id="brandId" className={CONTROL} {...form.register("brandId")}>
            <option value="">{t("noBrand")}</option>
            {(brands.data ?? []).map((brand) => (
              <option key={brand.id} value={brand.id}>
                {brand.name}
              </option>
            ))}
          </select>
        </Field>

        <Field
          label={t("category")}
          htmlFor="categoryId"
          hint={emptyHint(categories)}
          error={errors.categoryId?.message}
        >
          {/* Shown with ancestry — "Beverages / Water / Still" — because a tree routinely repeats a
              leaf name under two parents, and a flat list of "Other" asks the author to guess. */}
          <select id="categoryId" className={CONTROL} {...form.register("categoryId")}>
            <option value="">{t("noCategory")}</option>
            {categoryOptions.map((category) => (
              <option key={category.id} value={category.id}>
                {category.path}
              </option>
            ))}
          </select>
        </Field>

        <Field
          label={t("taxClass")}
          htmlFor="taxClassId"
          hint={emptyHint(taxClasses)}
          error={errors.taxClassId?.message}
        >
          <select id="taxClassId" className={CONTROL} {...form.register("taxClassId")}>
            <option value="">{t("noTaxClass")}</option>
            {(taxClasses.data ?? []).map((taxClass) => (
              <option key={taxClass.id} value={taxClass.id}>
                {taxClass.name}
              </option>
            ))}
          </select>
        </Field>

        <Field label={t("status")} htmlFor="status" error={errors.status?.message}>
          {/* Not a one-way door, unlike closing an outlet: seasonal lines come back, and forcing a
              new SKU to sell one again would orphan every order line pointing at this id. */}
          <select id="status" className={CONTROL} {...form.register("status")}>
            <option value="Active">{t("statusActive")}</option>
            <option value="Discontinued">{t("statusDiscontinued")}</option>
          </select>
        </Field>

        <Field
          label={t("unitOfMeasure")}
          htmlFor="unitOfMeasure"
          hint={t("unitOfMeasureHint")}
          error={errors.unitOfMeasure?.message}
        >
          <input id="unitOfMeasure" className={CONTROL} {...form.register("unitOfMeasure")} />
        </Field>

        <Field
          label={t("packSize")}
          htmlFor="packSize"
          hint={t("packSizeHint")}
          error={errors.packSize?.message}
        >
          <input
            id="packSize"
            type="number"
            min={1}
            step={1}
            className={CONTROL}
            aria-invalid={Boolean(errors.packSize)}
            aria-describedby={errors.packSize ? "packSize-error" : undefined}
            {...form.register("packSize")}
          />
        </Field>
      </div>

      <CustomFields
        definitions={definitions.data ?? []}
        control={form.control as never}
        errors={errors}
      />

      {refused.length > 0 ? (
        <ul role="alert" className="rounded-xl bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {refused.map((problem) => (
            <li key={problem}>{problem}</li>
          ))}
        </ul>
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
