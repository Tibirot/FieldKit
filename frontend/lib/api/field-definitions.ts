import { apiGet } from "@/lib/api/client";

/** The five things a tenant can declare a custom field to be (`CFG-01`). */
export type CustomFieldType = "Text" | "Number" | "Boolean" | "Date" | "Choice";

/**
 * One custom field a tenant has defined for an entity.
 *
 * Mirrors the API's `FieldDefinitionDescriptor`. The constraint fields are nullable because they
 * only apply to some types — `maxLength` to text, `minimum`/`maximum` to numbers, `options` to a
 * choice — and a definition carries whichever its type allows.
 */
export type FieldDefinition = {
  id: string;
  entity: "Outlet" | "Product" | "Order" | "Visit";
  key: string;
  label: string;
  type: CustomFieldType;
  required: boolean;
  options: string[];
  maxLength: number | null;
  minimum: number | null;
  maximum: number | null;
};

export function fetchFieldDefinitions(
  accessToken: string,
  entity: FieldDefinition["entity"],
  signal?: AbortSignal,
): Promise<FieldDefinition[]> {
  return apiGet<FieldDefinition[]>(
    `/api/config/field-definitions?entity=${entity}`,
    accessToken,
    signal,
  );
}

/**
 * The catalogue is reference data and changes when an admin changes it, so it caches like the rest.
 *
 * Keyed by entity as well as subject: outlets and products have separate catalogues, and a single
 * key would serve one form the other's fields.
 */
export const fieldDefinitionsKey = (subject: string, entity: FieldDefinition["entity"]) =>
  ["field-definitions", subject, entity] as const;
