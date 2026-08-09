import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import {
  resolvePromotion,
  type PromotionCandidate,
  type ResolvedPromotion,
} from "@/lib/pricing/promotion-resolver";

/**
 * The shared promotion vectors, run against the device mirror (`PRD-06`, `PRD-08`) — W7 slice 13.
 *
 * Same files as `PromotionResolutionVectorTests.cs`, read from `vectors/` rather than copied. The
 * hand-written file states the rules; the generated one is an **oracle** — its expectations came from
 * the C# engine — so a disagreement there is this mirror being wrong, not the rule being wrong.
 */
type VectorFile = {
  version: number;
  cases: {
    name: string;
    on: string;
    quantity: number;
    candidates: PromotionCandidate[];
    expected: ResolvedPromotion | null;
  }[];
};

function load(file: string): VectorFile {
  const path = fileURLToPath(new URL(`../../../vectors/pricing/${file}`, import.meta.url));

  return JSON.parse(readFileSync(path, "utf8")) as VectorFile;
}

const handWritten = load("promotion-resolution.v1.json");
const generated = load("promotion-resolution.generated.v1.json");

/**
 * Every money-shaped value in a file, wherever it hides, with a path for the failure message.
 *
 * More places than the price vectors have: a percentage on the candidate, one on each tier, one
 * inside a bundle, and the same again on the expectation. A guard that only checked the top level
 * would pass a file whose tiers had been written as numbers — which is exactly where a hand edit
 * would put them.
 */
function amountsIn(file: VectorFile): { path: string; value: unknown }[] {
  const found: { path: string; value: unknown }[] = [];

  const take = (path: string, value: unknown) => {
    if (value !== undefined && value !== null) found.push({ path, value });
  };

  for (const vector of file.cases) {
    vector.candidates.forEach((candidate, index) => {
      const at = `${vector.name} › candidate ${index}`;

      take(`${at}.percentOff`, candidate.percentOff);
      take(`${at}.amountOff`, candidate.amountOff);
      take(`${at}.bundle.getPercentOff`, candidate.bundle?.getPercentOff);

      (candidate.tiers ?? []).forEach((tier, tierIndex) => {
        take(`${at}.tiers[${tierIndex}].percentOff`, tier.percentOff);
        take(`${at}.tiers[${tierIndex}].amountOff`, tier.amountOff);
      });
    });

    if (vector.expected) {
      take(`${vector.name} › expected.percentOff`, vector.expected.percentOff);
      take(`${vector.name} › expected.amountOff`, vector.expected.amountOff);
      take(`${vector.name} › expected.bundle.getPercentOff`, vector.expected.bundle?.getPercentOff);
    }
  }

  return found;
}

describe.each([
  ["hand-written", handWritten],
  ["generated", generated],
])("promotion resolution vectors (%s)", (_label, file) => {
  it("loads the file the C# engine reads", () => {
    // Guards the wiring: a broken path or an emptied file turns every case below into zero tests.
    expect(file.version).toBe(1);
    expect(file.cases.length).toBeGreaterThanOrEqual(15);
    expect(new Set(file.cases.map((vector) => vector.name)).size).toBe(file.cases.length);
  });

  it("carries every amount and percentage as a string, never a JSON number", () => {
    // `JSON.parse` would turn a bare 15.00 into a float before the engine saw it, and the suite
    // would then be checking that both sides make the same rounding error.
    const amounts = amountsIn(file);

    expect(amounts.length).toBeGreaterThan(0);

    for (const { path, value } of amounts) {
      expect(typeof value, `${path} must be a string`).toBe("string");
    }
  });

  it.each(file.cases.map((vector) => [vector.name, vector] as const))("%s", (_name, vector) => {
    const actual = resolvePromotion(vector.candidates, vector.quantity, vector.on);

    if (vector.expected === null) {
      expect(actual).toBeNull();
      return;
    }

    expect(actual).not.toBeNull();
    expect(actual!.promotionId).toBe(vector.expected.promotionId);
    expect(actual!.type).toBe(vector.expected.type);
    expect(actual!.priority).toBe(vector.expected.priority);

    // Exact strings, scale included — "10.00" and "10.0" are the same number and not the same
    // answer, and a tiered promotion resolving to the wrong tier shows up here first.
    expect(actual!.percentOff).toBe(vector.expected.percentOff);
    expect(actual!.amountOff).toBe(vector.expected.amountOff);
    expect(actual!.currency).toBe(vector.expected.currency);
    expect(actual!.bundle).toEqual(vector.expected.bundle);
  });
});

/**
 * Cases about *this* language, which a shared vector file cannot express.
 */
describe("resolvePromotion, in the ways only TypeScript can fail", () => {
  const flat: PromotionCandidate = {
    promotionId: "0195f000-0000-7000-8000-000000000001",
    type: "PercentOff",
    priority: 10,
    validFrom: "2026-06-01",
    validTo: null,
    percentOff: "15.00",
  };

  it("resolves to the tier's discount, not the candidate's", () => {
    // A tiered promotion may carry a candidate-level value as well — the wire shape allows it — and
    // the tier is the one that was earned. Reversing the fallback would answer with a discount the
    // quantity did not reach, which is the shape of "won the contest and then did nothing useful".
    //
    // The zero is deliberate: a tier authored at 0% is a statement, and it has to survive being
    // read. (`??` rather than `||` says that in the code — though in practice both behave the same
    // here, since every amount is a non-empty string. The operator is chosen for what it means, not
    // because `||` currently misbehaves.)
    const tiered: PromotionCandidate = {
      ...flat,
      type: "VolumeTiered",
      percentOff: "15.00",
      tiers: [
        { minQuantity: 6, percentOff: "0.00" },
        { minQuantity: 12, percentOff: "7.50" },
      ],
    };

    expect(resolvePromotion([tiered], 10, "2026-06-15")!.percentOff).toBe("0.00");
    expect(resolvePromotion([tiered], 12, "2026-06-15")!.percentOff).toBe("7.50");
  });

  it("breaks a tie the same way whatever case the ids are spelled in", () => {
    // Letter against letter in opposite cases, which is the only pair that separates a raw string
    // comparison from a normalised one: in ASCII 'B' (0x42) < 'a' (0x61), while b > a as hex.
    const bigger = { ...flat, promotionId: "0195B000-0000-7000-8000-000000000001" };
    const smaller = { ...flat, promotionId: "0195a000-0000-7000-8000-000000000001" };

    expect(resolvePromotion([bigger, smaller], 1, "2026-06-15")!.promotionId).toBe(
      bigger.promotionId,
    );
    expect(resolvePromotion([smaller, bigger], 1, "2026-06-15")!.promotionId).toBe(
      bigger.promotionId,
    );
  });

  it("does not care what order the candidates arrive in", () => {
    const louder: PromotionCandidate = {
      ...flat,
      promotionId: "0195f000-0000-7000-8000-000000000002",
      priority: 20,
      percentOff: "5.00",
    };

    // The lower-priority candidate offers the bigger discount on purpose: priority decides, not
    // size, and a fold that kept the first match would pass every single-candidate case in the file.
    expect(resolvePromotion([flat, louder], 1, "2026-06-15")!.percentOff).toBe("5.00");
    expect(resolvePromotion([louder, flat], 1, "2026-06-15")!.percentOff).toBe("5.00");
  });

  it("compares dates as days, not as instants", () => {
    expect(resolvePromotion([flat], 1, "2026-06-01")).not.toBeNull();
    expect(resolvePromotion([flat], 1, "2026-05-31")).toBeNull();
  });

  it("survives a candidate whose optional fields are simply absent", () => {
    // JSON omits nulls in places C# spells them out, and `tiers` / `bundle` / `amountOff` are all
    // optional in the wire shape. A mirror that assumed they were present would throw on data the
    // server considers well-formed — and the resolved shape still has to say `null` rather than
    // `undefined`, because that is what the vectors compare against.
    const bare = {
      promotionId: "0195f000-0000-7000-8000-000000000003",
      type: "PercentOff",
      priority: 1,
      validFrom: "2026-06-01",
      validTo: null,
      percentOff: "5.00",
    } as PromotionCandidate;

    const resolved = resolvePromotion([bare], 1, "2026-06-15")!;

    expect(resolved.amountOff).toBeNull();
    expect(resolved.currency).toBeNull();
    expect(resolved.bundle).toBeNull();
  });
});
