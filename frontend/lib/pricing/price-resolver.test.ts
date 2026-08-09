import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { resolvePrice, type PriceCandidate, type ResolvedPrice } from "@/lib/pricing/price-resolver";

/**
 * The shared pricing vectors, run against the device mirror (`PRD-04`, `PRD-08`) — W7 slice 12.
 *
 * **The same files the C# engine runs**, read from `vectors/` rather than copied here. That is the
 * whole mechanism: two implementations, one set of answers, and a disagreement names itself as a
 * failing case rather than as a bug report six months later.
 *
 * Read from disk rather than imported, deliberately. A JSON import would let the bundler decide the
 * parse, and this suite exists partly to check *how* the file parses — see the format guard below.
 */
type VectorFile = {
  version: number;
  cases: {
    name: string;
    on: string;
    candidates: PriceCandidate[];
    expected: ResolvedPrice | null;
  }[];
};

function load(file: string): VectorFile {
  const path = fileURLToPath(new URL(`../../../vectors/pricing/${file}`, import.meta.url));

  return JSON.parse(readFileSync(path, "utf8")) as VectorFile;
}

const handWritten = load("price-resolution.v1.json");
const generated = load("price-resolution.generated.v1.json");

/** Every amount in a vector file, wherever it appears, with a path for the failure message. */
function amountsIn(file: VectorFile): { path: string; value: unknown }[] {
  return file.cases.flatMap((vector) => [
    ...vector.candidates.map((candidate, index) => ({
      path: `${vector.name} › candidate ${index}`,
      value: candidate.amount as unknown,
    })),
    ...(vector.expected
      ? [{ path: `${vector.name} › expected`, value: vector.expected.amount as unknown }]
      : []),
  ]);
}

describe.each([
  ["hand-written", handWritten],
  ["generated", generated],
])("price resolution vectors (%s)", (_label, file) => {
  it("loads the file the C# engine reads", () => {
    // Guards the wiring, not the engine. If the path breaks or the file empties, `it.each` below
    // silently becomes zero tests — a green suite that checked nothing. This is what goes red.
    expect(file.version).toBe(1);
    expect(file.cases.length).toBeGreaterThanOrEqual(10);
    expect(new Set(file.cases.map((vector) => vector.name)).size).toBe(file.cases.length);
  });

  it("carries every amount as a string, never a JSON number", () => {
    // The format rule `vectors/README.md` states, enforced here because this is the language it
    // exists to protect: `JSON.parse` turns a bare `12.50` into an IEEE-754 double **before the
    // engine under test ever sees it**, and the suite would then be checking that both sides make
    // the same rounding error. C# refuses the number token in its converter; this is the mirror of
    // that refusal.
    for (const { path, value } of amountsIn(file)) {
      expect(typeof value, `${path} must be a string`).toBe("string");
    }
  });

  it.each(file.cases.map((vector) => [vector.name, vector] as const))(
    "%s",
    (_name, vector) => {
      const actual = resolvePrice(vector.candidates, vector.on);

      if (vector.expected === null) {
        expect(actual).toBeNull();
        return;
      }

      expect(actual).not.toBeNull();
      expect(actual!.priceListId).toBe(vector.expected.priceListId);
      expect(actual!.scope).toBe(vector.expected.scope);
      expect(actual!.currency).toBe(vector.expected.currency);

      // The exact string, scale included. "12.5000" and "12.50" are the same number and not the
      // same answer to give a rep, and the file records the scale on purpose (BR-PRD-8).
      expect(actual!.amount).toBe(vector.expected.amount);
    },
  );
});

/**
 * Cases the vector files cannot express, because they are about *this* language.
 *
 * Everything above is shared with C#. What follows is where a JavaScript implementation can go wrong
 * on its own, and so has no counterpart in `PriceResolutionVectorTests.cs`.
 */
describe("resolvePrice, in the ways only TypeScript can fail", () => {
  const base: PriceCandidate = {
    priceListId: "0195f000-0000-7000-8000-000000000001",
    scope: "Channel",
    currency: "EUR",
    effectiveFrom: "2026-01-01",
    effectiveTo: null,
    amount: "12.50",
  };

  it("compares dates as days, not as instants", () => {
    // `new Date("2026-03-15")` is midnight UTC, which is the previous day west of Greenwich — so a
    // Date-based implementation would answer differently depending on where the phone is. The
    // string comparison has no timezone to get wrong, and this is the case that would expose it: a
    // window starting exactly on the day asked about.
    expect(resolvePrice([base], "2026-01-01")).not.toBeNull();
    expect(resolvePrice([base], "2025-12-31")).toBeNull();
  });

  it("breaks a tie the same way whatever case the ids are spelled in", () => {
    // In ASCII '0'–'9' < 'A'–'F' < 'a'–'f'. The pair has to be **letter against letter, in opposite
    // cases**, and the upper-cased one has to be the larger value — otherwise the raw comparison and
    // the normalised one agree by accident and the test discriminates nothing. (My first attempt did
    // exactly that: digit-vs-letter, where uppercase letters still sort above digits. It is the same
    // trap `vectors/README.md` records for the byte-order pair, one level down.)
    const bigger = { ...base, priceListId: "0195B000-0000-7000-8000-000000000001" };
    const smaller = { ...base, priceListId: "0195a000-0000-7000-8000-000000000001" };

    // b > a, so `bigger` wins. Compared raw, 'B' (0x42) < 'a' (0x61) and `smaller` would.
    expect(resolvePrice([bigger, smaller], "2026-06-01")!.priceListId).toBe(bigger.priceListId);
    expect(resolvePrice([smaller, bigger], "2026-06-01")!.priceListId).toBe(bigger.priceListId);
  });

  it("does not care what order the candidates arrive in", () => {
    // A fold that kept the first match rather than the best one would pass every single-candidate
    // case in the file and fail here. Both orders, because the naive bug is order-dependent.
    const outlet: PriceCandidate = { ...base, scope: "Outlet", priceListId: "0195f000-0000-7000-8000-000000000002", amount: "9.99" };

    expect(resolvePrice([base, outlet], "2026-06-01")!.amount).toBe("9.99");
    expect(resolvePrice([outlet, base], "2026-06-01")!.amount).toBe("9.99");
  });

  it("returns the amount verbatim rather than reformatting it", () => {
    // Resolution picks; it does not compute. A mirror that parsed the amount into a Decimal and
    // printed it back would turn "12.50" into "12.5" — the same number, a different answer, and a
    // vector failure that looks like a rounding bug.
    expect(resolvePrice([{ ...base, amount: "12.50" }], "2026-06-01")!.amount).toBe("12.50");
    expect(resolvePrice([{ ...base, amount: "0.125" }], "2026-06-01")!.amount).toBe("0.125");
  });
});
