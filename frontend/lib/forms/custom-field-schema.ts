import { z } from "zod";

import type { FieldDefinition } from "@/lib/api/field-definitions";

/**
 * The messages a schema needs, supplied by the caller.
 *
 * Passed in rather than written here because this file has no locale. Zod's own defaults are
 * developer text in one language — *"Too small: expected string to have >=1 characters"* — which is
 * unreadable to a user and a hole in an app that ships English and Romanian (ADR-0010). Making them
 * a parameter keeps the rules here and the words in the message catalogue.
 */
export type ValidationMessages = {
  /**
   * Deliberately not "{field} is required".
   *
   * The message renders directly under its own label, so naming the field again is redundant — and
   * interpolating it produces ungrammatical Romanian, where a noun needs its definite article
   * ("Codul", not "Cod"). Any language with grammatical gender or cases has the same problem, and no
   * amount of template wrangling fixes it from the outside.
   */
  required: string;
  tooLong: (max: number) => string;
  atMost: (max: number) => string;
  atLeast: (min: number) => string;
  mustBeNumber: string;
  notAnOption: string;
  mustBeDate: string;
  notAnEmail: string;
  notACountry: string;
};

/**
 * Turns a tenant's field definitions into a schema (`CFG-01`, `CFG-02`).
 *
 * **Built from the descriptor, never written by hand.** That is the whole discipline here: the
 * server owns these rules (`BR-CFG-3` — it re-validates every write authoritatively), so a schema
 * anyone typed out would be a second declaration free to drift from the first. Derived from the same
 * descriptor the server answered with, it cannot.
 *
 * What it buys over the raw HTML constraints alone: an error attached to a *field*, so the form can
 * point at the control rather than list a sentence at the top.
 */
export function customFieldSchema(
  definitions: readonly FieldDefinition[],
  messages: ValidationMessages,
) {
  const shape: Record<string, z.ZodTypeAny> = {};

  for (const definition of definitions) {
    shape[definition.key] = optionalise(definition, forType(definition, messages), messages);
  }

  // Passthrough, not strict: an outlet can hold a value for a field that was deleted from the
  // catalogue after it was written, and this schema's job is to check what a tenant declared — not
  // to delete history it no longer describes.
  return z.object(shape).passthrough();
}

function forType(definition: FieldDefinition, messages: ValidationMessages): z.ZodTypeAny {
  switch (definition.type) {
    case "Number": {
      let schema = z.number({ message: messages.mustBeNumber });

      // The bounds the tenant declared, with the number in the message — "at most 50" is actionable
      // in a way "invalid" is not.
      if (definition.minimum !== null) {
        schema = schema.min(definition.minimum, { message: messages.atLeast(definition.minimum) });
      }

      if (definition.maximum !== null) {
        schema = schema.max(definition.maximum, { message: messages.atMost(definition.maximum) });
      }

      return schema;
    }

    case "Boolean":
      return z.boolean();

    case "Choice":
      // The options, exactly. An empty list would make `z.enum` throw at construction, and the API
      // already refuses to define a choice without options — but a schema that explodes on
      // malformed input from another service is a worse failure than one that accepts a string.
      return definition.options.length > 0
        ? z.enum(definition.options as [string, ...string[]], { message: messages.notAnOption })
        : z.string();

    case "Date":
      // The one format that sorts and parses the same everywhere, matching the server's rule — an
      // instant would store a moment for something the tenant means as a day.
      return z.string().regex(/^\d{4}-\d{2}-\d{2}$/, { message: messages.mustBeDate });

    case "Text":
      return definition.maxLength === null
        ? z.string()
        : z.string().max(definition.maxLength, { message: messages.tooLong(definition.maxLength) });
  }
}

/**
 * Lets an optional field be absent, and makes a required one say so in its own words.
 *
 * Absent means `undefined`, `null` or `""` — the three ways an emptied control can report itself.
 * The API agrees: a missing key and a JSON null are the same thing to `CustomFieldValidator`.
 */
function optionalise(
  definition: FieldDefinition,
  schema: z.ZodTypeAny,
  messages: ValidationMessages,
): z.ZodTypeAny {
  // `nullish`, not `optional`: an emptied control reports `null` (see `read` in CustomFields for
  // why it cannot report `undefined`), and `.optional()` alone would refuse it.
  if (!definition.required) return schema.nullish();

  // A required boolean is satisfied by `false`. `required` in the catalogue means "must have an
  // answer", and no is an answer — which is exactly the distinction a checkbox's own `required`
  // attribute gets wrong.
  if (definition.type === "Boolean") return schema;

  // Checked in one pass rather than composed, because neither half works alone: the type schema on
  // its own rejects an empty required choice as "not one of the options" (true, and useless — the
  // problem is that nothing was chosen), while `.optional().refine(…)` never runs at all when the
  // key is simply absent, which is exactly how an untouched control arrives.
  return z.custom().superRefine((value, ctx) => {
    if (value === undefined || value === null || value === "") {
      ctx.addIssue({ code: "custom", message: messages.required });
      return;
    }

    const parsed = schema.safeParse(value);

    // The type's own message, so a required number over its maximum still says "at most 50" rather
    // than collapsing every failure into "required". Re-raised as a custom issue: the original
    // carries Zod's internal shape, which `addIssue` will not take back verbatim.
    for (const issue of parsed.error?.issues ?? []) {
      ctx.addIssue({ code: "custom", message: issue.message });
    }
  });
}
