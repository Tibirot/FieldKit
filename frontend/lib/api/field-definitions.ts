import { apiDelete, apiGet, apiSend } from "@/lib/api/client";

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

/**
 * A definition as an admin authors it.
 *
 * The constraints are all optional and all nullable because a definition carries only the ones its
 * type allows — a `maxLength` on a number would validate nothing and render nowhere, and would
 * become quietly authoritative again if the type were later changed to text.
 *
 * `entity` and `key` appear only on the create side. Both are fixed once a definition exists: the
 * key is the JSONB property name already written into every row, so renaming it would orphan every
 * value stored under the old one ([Configuration §6.1](../../../docs/product/14-configuration.md)).
 */
export type FieldDefinitionWrite = {
  key: string;
  label: string;
  type: CustomFieldType;
  required: boolean;
  options?: string[] | null;
  maxLength?: number | null;
  minimum?: number | null;
  maximum?: number | null;
};

export function createFieldDefinition(
  accessToken: string,
  entity: FieldDefinition["entity"],
  definition: FieldDefinitionWrite,
): Promise<FieldDefinition> {
  return apiSend<FieldDefinition>("POST", "/api/config/field-definitions", accessToken, {
    entity,
    ...definition,
  });
}

/**
 * No entity and no key — the API's update contract has neither, for the reason above.
 *
 * The key is dropped here rather than left out by the caller, so that a form holding one cannot
 * send it and be silently ignored: the value it names is already written into every row, and an
 * accepted-looking rename that changed nothing is worse than no rename at all.
 */
export function updateFieldDefinition(
  accessToken: string,
  id: string,
  definition: FieldDefinitionWrite,
): Promise<FieldDefinition> {
  return apiSend<FieldDefinition>("PUT", `/api/config/field-definitions/${id}`, accessToken, {
    label: definition.label,
    type: definition.type,
    required: definition.required,
    options: definition.options,
    maxLength: definition.maxLength,
    minimum: definition.minimum,
    maximum: definition.maximum,
  });
}

/**
 * Stops a field being collected. It is not a redaction.
 *
 * The values already written under this key stay in each entity's JSONB and simply stop being
 * described — Configuration cannot reach into another module's tables to clean them (ADR-0005) and
 * should not. Outlets rejects undescribed keys on the *next* write, so those values persist until
 * the outlet is next saved and then disappear. The screen says so before anyone presses this.
 */
export function deleteFieldDefinition(accessToken: string, id: string): Promise<void> {
  return apiDelete(`/api/config/field-definitions/${id}`, accessToken);
}
