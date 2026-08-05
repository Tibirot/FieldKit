import { describe, expect, it } from "vitest";

import type { FieldDefinition } from "@/lib/api/field-definitions";
import { customFieldSchema } from "@/lib/forms/custom-field-schema";

const define = (over: Partial<FieldDefinition>): FieldDefinition => ({
  id: "f1",
  entity: "Outlet",
  key: "field",
  label: "Field",
  type: "Text",
  required: false,
  options: [],
  maxLength: null,
  minimum: null,
  maximum: null,
  ...over,
});

/**
 * The words, supplied by the caller so this file can test rules rather than wording.
 *
 * Deliberately not the real catalogue: asserting on "Must be at most 50." would make these tests
 * fail when someone improves the sentence, which is not a regression. Asserting on the *shape*
 * ("atMost:50") fails only when the wrong rule fired.
 */
const MESSAGES = {
  required: "required",
  tooLong: (max: number) => `tooLong:${max}`,
  atMost: (max: number) => `atMost:${max}`,
  atLeast: (min: number) => `atLeast:${min}`,
  mustBeNumber: "mustBeNumber",
  notAnOption: "notAnOption",
  mustBeDate: "mustBeDate",
};

/** The first message for a key, or undefined when the value was accepted. */
function reject(definitions: FieldDefinition[], values: Record<string, unknown>): string | undefined {
  const result = customFieldSchema(definitions, MESSAGES).safeParse(values);
  return result.success ? undefined : result.error.issues[0]?.message;
}

describe("customFieldSchema", () => {
  it("enforces the bounds the tenant declared, and says which they are", () => {
    // The number is in the message because "at most 50" is actionable and "invalid" is not.
    const chillers = [define({ key: "chillers", type: "Number", minimum: 0, maximum: 50 })];

    expect(reject(chillers, { chillers: 12 })).toBeUndefined();
    expect(reject(chillers, { chillers: 900 })).toBe("atMost:50");
    expect(reject(chillers, { chillers: -1 })).toBe("atLeast:0");
  });

  it("accepts only the options a choice was defined with", () => {
    const ownership = [define({ key: "ownership", type: "Choice", options: ["independent", "franchise"] })];

    expect(reject(ownership, { ownership: "franchise" })).toBeUndefined();

    // Ordinal, like the server: a tenant that meant Franchise and franchise as one value should
    // have defined one of them.
    expect(reject(ownership, { ownership: "Franchise" })).toBe("notAnOption");
    expect(reject(ownership, { ownership: "cooperative" })).toBe("notAnOption");
  });

  it("wants a day, not an instant", () => {
    const refit = [define({ key: "refit", type: "Date" })];

    expect(reject(refit, { refit: "2026-03-01" })).toBeUndefined();
    expect(reject(refit, { refit: "2026-03-01T00:00:00Z" })).toBe("mustBeDate");
    expect(reject(refit, { refit: "01/03/2026" })).toBe("mustBeDate");
  });

  it("lets an optional field be absent", () => {
    const note = [define({ key: "note", type: "Text" })];

    expect(reject(note, {})).toBeUndefined();
    expect(reject(note, { note: undefined })).toBeUndefined();
  });

  it("names the field a tenant left empty", () => {
    const ownership = [
      define({ key: "ownership", label: "Ownership", type: "Choice", options: ["independent"], required: true }),
    ];

    expect(reject(ownership, {})).toBe("required");
    expect(reject(ownership, { ownership: "independent" })).toBeUndefined();
  });

  it("treats no as an answer for a required boolean", () => {
    // `required` in the catalogue means "must have an answer" — which is exactly what a checkbox's
    // own `required` attribute gets wrong, since it means "must be ticked".
    const parking = [define({ key: "parking", type: "Boolean", required: true })];

    expect(reject(parking, { parking: false })).toBeUndefined();
    expect(reject(parking, { parking: true })).toBeUndefined();
  });

  it("keeps a value whose definition has since been deleted", () => {
    // An outlet can hold a field the catalogue no longer describes. This schema checks what a tenant
    // declared; deleting history it stopped describing is not its job — and is not the server's
    // behaviour either.
    const result = customFieldSchema([define({ key: "note" })], MESSAGES).safeParse({ note: "x", retired: 3 });

    expect(result.success).toBe(true);
    expect(result.data).toMatchObject({ retired: 3 });
  });

  it("survives a choice with no options rather than throwing at construction", () => {
    // The API refuses to define one, so this is defence against a malformed answer — and a schema
    // that explodes on bad input from another service fails worse than one that accepts a string.
    expect(() =>
      customFieldSchema([define({ key: "broken", type: "Choice", options: [] })], MESSAGES),
    ).not.toThrow();
  });
});
