import { createTranslator } from "next-intl";
import { describe, expect, it } from "vitest";

import type { FieldProblem } from "@/lib/api/client";
import { refusalText, refusalTexts, type RefusalTranslator } from "@/lib/api/refusals";

import en from "../../messages/en.json";
import ro from "../../messages/ro.json";

/** The real catalogue through the real formatter — a stub would not prove the ICU arguments line up. */
function translator(locale: "en" | "ro"): RefusalTranslator {
  return createTranslator({
    locale,
    messages: locale === "en" ? en : ro,
    namespace: "Refusals",
  }) as unknown as RefusalTranslator;
}

function problem(over: Partial<FieldProblem> = {}): FieldProblem {
  return {
    field: "name",
    message: "A price list named 'Modern Trade' already exists.",
    code: "product.priceList.nameTaken",
    args: { name: "Modern Trade" },
    ...over,
  };
}

describe("refusalText", () => {
  it("says a known refusal in the reader's language", () => {
    // The whole point of ADR-0012: the server said which rule and about what, and the language is
    // chosen here rather than at the time the refusal was created.
    expect(refusalText(translator("ro"), problem())).toBe(
      "Există deja o listă de prețuri numită „Modern Trade”.",
    );
  });

  it("falls back to the server's English when the code has no entry", () => {
    // The designed behaviour, not a safety net — it is what lets modules migrate one at a time, and
    // what stops a server rule shipped today from showing a raw dotted name until someone translates
    // it. `product.customField.wrongType` is deliberately absent from the catalogue.
    const unknown = problem({
      code: "product.customField.wrongType",
      message: "'chiller_count' must be a number.",
    });

    expect(refusalText(translator("ro"), unknown)).toBe("'chiller_count' must be a number.");
  });

  it("falls back for a module that emits no code at all", () => {
    // Org, Outlets, IAM and Configuration are not migrated yet, so their refusals arrive with a
    // message and nothing else. They must keep working, unchanged.
    const uncoded = problem({ code: undefined, args: undefined, message: "That channel is in use." });

    expect(refusalText(translator("ro"), uncoded)).toBe("That channel is in use.");
  });

  it("puts the server's args into the catalogue's placeholders", () => {
    const counted = problem({
      code: "product.priceList.outletMissing",
      args: { count: "3" },
      message: "3 outlet(s) do not exist.",
    });

    expect(refusalText(translator("en"), counted)).toBe("3 outlet(s) do not exist.");
    expect(refusalText(translator("ro"), counted)).toBe("3 punct(e) de vânzare nu există.");
  });

  it("never turns an amount into a number on the way through", () => {
    // BR-PRD-8 reaches the refusal path too: "0.4996" is the value, and a catalogue entry that used
    // an ICU `number` skeleton — or a resolver that coerced the arg — would render "0.5".
    const amount = problem({
      code: "product.price.notANumber",
      args: { amount: "0.4996" },
      message: "'0.4996' is not a decimal amount.",
    });

    expect(refusalText(translator("en"), amount)).toContain("0.4996");
    expect(refusalText(translator("ro"), amount)).toContain("0.4996");
  });

  it("keeps the API's order when there are several", () => {
    const t = translator("en");
    const problems = [
      problem({ code: "product.priceList.nameRequired", args: {} }),
      problem({ code: "product.priceList.windowInverted", args: {} }),
    ];

    expect(refusalTexts(t, problems)).toEqual([
      "A price list needs a name.",
      "A price list ends after it starts.",
    ]);
  });
});
