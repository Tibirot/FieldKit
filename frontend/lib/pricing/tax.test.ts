import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { Money } from "@/lib/pricing/money";
import { applyTax, resolveTaxRate, type TaxRateCandidate } from "@/lib/pricing/tax";

/**
 * The shared tax vectors, run against the device mirror (`PRD-07`, `PRD-08`) — W7 slice 14.
 *
 * The last of the three mirrors, and the first that does **arithmetic** rather than selection. That
 * is why this file matters most: a resolver disagreeing picks a different row, which is visible. An
 * arithmetic engine disagreeing is off by a cent, which is not — until somebody reconciles a ledger.
 */
type ResolutionVector = {
  name: string;
  on: string;
  candidates: TaxRateCandidate[];
  expected: { taxRateId: string; percentage: string } | null;
};

type ApplicationVector = {
  name?: string;
  net: string;
  currency: string;
  percentage: string;
  expected: { net: string; tax: string; gross: string };
};

type TaxFile = {
  version: number;
  resolution?: ResolutionVector[];
  application: ApplicationVector[];
};

function load(file: string): TaxFile {
  const path = fileURLToPath(new URL(`../../../vectors/pricing/${file}`, import.meta.url));

  return JSON.parse(readFileSync(path, "utf8")) as TaxFile;
}

const handWritten = load("tax.v1.json");
const generated = load("tax-application.generated.v1.json");

/** Everything money-shaped in an application case, for the format guard. */
function amountsOf(vector: ApplicationVector): { path: string; value: unknown }[] {
  const label = vector.name ?? `${vector.net} @ ${vector.percentage}`;

  return [
    { path: `${label}.net`, value: vector.net as unknown },
    { path: `${label}.percentage`, value: vector.percentage as unknown },
    { path: `${label}.expected.net`, value: vector.expected.net as unknown },
    { path: `${label}.expected.tax`, value: vector.expected.tax as unknown },
    { path: `${label}.expected.gross`, value: vector.expected.gross as unknown },
  ];
}

describe("tax resolution vectors", () => {
  const cases = handWritten.resolution ?? [];

  it("loads the file the C# engine reads", () => {
    expect(handWritten.version).toBe(1);
    expect(cases.length).toBeGreaterThanOrEqual(9);
    expect(new Set(cases.map((vector) => vector.name)).size).toBe(cases.length);
  });

  it.each(cases.map((vector) => [vector.name, vector] as const))("%s", (_name, vector) => {
    const actual = resolveTaxRate(vector.candidates, vector.on);

    if (vector.expected === null) {
      // Unknown, not zero. A caller treating this as 0% invoices untaxed and looks deliberate.
      expect(actual).toBeNull();
      return;
    }

    expect(actual).not.toBeNull();
    expect(actual!.taxRateId).toBe(vector.expected.taxRateId);
    expect(actual!.percentage).toBe(vector.expected.percentage);
  });
});

describe.each([
  ["hand-written", handWritten],
  ["generated", generated],
])("tax application vectors (%s)", (_label, file) => {
  it("loads the file the C# engine reads", () => {
    expect(file.version).toBe(1);
    expect(file.application.length).toBeGreaterThanOrEqual(10);
  });

  it("carries every amount and percentage as a string, never a JSON number", () => {
    // The rule this whole suite depends on: `JSON.parse` turns a bare 12.99 into a float **before**
    // the engine sees it, and a parity suite would then be checking that both sides make the same
    // rounding error. This is the file where that would be least visible and most expensive.
    for (const vector of file.application) {
      for (const { path, value } of amountsOf(vector)) {
        expect(typeof value, `${path} must be a string`).toBe("string");
      }
    }
  });

  it.each(
    file.application.map(
      (vector, index) =>
        [vector.name ?? `${index}: ${vector.net} @ ${vector.percentage}%`, vector] as const,
    ),
  )("%s", (_name, vector) => {
    const actual = applyTax(Money.of(vector.net, vector.currency), vector.percentage);

    // Exact strings at the currency's scale, which is what an invoice prints.
    expect(actual.net.toWire()).toBe(vector.expected.net);
    expect(actual.tax.toWire()).toBe(vector.expected.tax);
    expect(actual.gross.toWire()).toBe(vector.expected.gross);

    // …and the *values* are at that scale too, not merely printed at it.
    //
    // C#'s side of this suite compares `decimal.ToString()`, and a .NET decimal carries its scale —
    // so an unrounded 2.4681 fails there. A JS decimal does not, and `toWire` rounds on the way out,
    // so the three assertions above would pass on a mirror that never rounded anything. This is the
    // difference in the languages, not in the rule, and it has to be closed explicitly.
    for (const [label, money] of [["net", actual.net], ["tax", actual.tax], ["gross", actual.gross]] as const) {
      expect(
        money.equals(Money.of(money.toWire(), vector.currency)),
        `${label} carries more precision than ${vector.currency} has`,
      ).toBe(true);
    }
  });

  it("always produces three numbers that add up", () => {
    // A property over every case rather than a case of its own: an invoice shows net, tax and gross,
    // and a customer adds the first two. Any scheme that computed gross independently — net × 1.19 —
    // can break this on a case nobody wrote down.
    for (const vector of file.application) {
      const { net, tax, gross } = applyTax(Money.of(vector.net, vector.currency), vector.percentage);

      expect(gross.equals(net.add(tax)), `${vector.net} @ ${vector.percentage}%`).toBe(true);
    }
  });
});

/**
 * Cases the shared files cannot express — currencies they do not carry, and JavaScript's own hazards.
 */
describe("applyTax, beyond what the vectors cover", () => {
  it("rounds a half-cent away from zero at both steps", () => {
    // The single most important case in `tax.v1.json`, restated here against the engine directly:
    // 1.00 × 4.5% is exactly half a cent. Half-up gives 0.05; banker's rounding gives 0.04, and a
    // device disagreeing with the server by a cent is a reconciliation someone chases.
    const applied = applyTax(Money.of("1.00", "EUR"), "4.50");

    expect(applied.tax.toWire()).toBe("0.05");
    expect(applied.gross.toWire()).toBe("1.05");
  });

  it("rounds the tax itself, not merely the way it is printed", () => {
    // Found by mutation: deleting the tax's `.round()` broke **nothing**, because `toWire` formats
    // to the currency's scale and rounds on the way out — so every vector still passed while the
    // value carried 2.4681.
    //
    // It matters because BR-PRD-9 rounds **per line**: a caller summing twenty tax lines adds the
    // rounded ones, and a mirror holding unrounded values would drift against the server by cents
    // that no single line could explain. Asking for more decimals than the currency has is what
    // makes the difference visible.
    const applied = applyTax(Money.of("12.99", "EUR"), "19.00");

    expect(applied.tax.toWire()).toBe("2.47");
    expect(applied.tax.toWire(4)).toBe("2.4700");
    expect(applied.gross.toWire(4)).toBe("15.4600");
  });

  it("rounds the net line before taxing it", () => {
    // The order that makes the printed net and the tax agree. Taxing the unrounded 10.005 would give
    // 1.90 on a line whose net reads 10.01 — three numbers that no longer add up the way an invoice
    // claims. Rounding first gives 10.01 × 19% = 1.9019 → 1.90, and 11.91 gross.
    const applied = applyTax(Money.of("10.005", "EUR"), "19.00");

    expect(applied.net.toWire()).toBe("10.01");
    expect(applied.tax.toWire()).toBe("1.90");
    expect(applied.gross.toWire()).toBe("11.91");
  });

  it("taxes a currency with no minor unit in whole units", () => {
    // Every generated case is EUR, so the currency table only earns its keep here. 1000 JPY at 10%
    // is 100 yen — not 100.00, which is a fraction of a unit no invoice can express.
    const applied = applyTax(Money.of("1000", "JPY"), "10.00");

    expect(applied.net.toWire()).toBe("1000");
    expect(applied.tax.toWire()).toBe("100");
    expect(applied.gross.toWire()).toBe("1100");
  });

  it("taxes a three-decimal currency to three decimals", () => {
    // 1.2345 KWD rounds to 1.235 first, then 5% of that is 0.061725 → 0.062.
    const applied = applyTax(Money.of("1.2345", "KWD"), "5.00");

    expect(applied.net.toWire()).toBe("1.235");
    expect(applied.tax.toWire()).toBe("0.062");
    expect(applied.gross.toWire()).toBe("1.297");
  });

  it("leaves a zero-rated line alone, and says zero rather than nothing", () => {
    const applied = applyTax(Money.of("12.50", "EUR"), "0.00");

    expect(applied.tax.toWire()).toBe("0.00");
    expect(applied.gross.toWire()).toBe("12.50");
  });

  it("refuses to mix currencies, because gross is an addition", () => {
    // Not a case anyone would write deliberately — it is what happens if `applyTax` ever built the
    // tax in a different currency from the net. Money refuses, rather than inventing a rate.
    expect(() => Money.of("1", "EUR").add(Money.of("1", "USD"))).toThrow(/different currencies/);
  });
});

describe("resolveTaxRate, in the ways only TypeScript can fail", () => {
  const base: TaxRateCandidate = {
    taxRateId: "0195f000-0000-7000-8000-000000000001",
    percentage: "19.00",
    effectiveFrom: "2026-01-01",
    effectiveTo: null,
  };

  it("compares dates as days, not as instants", () => {
    expect(resolveTaxRate([base], "2026-01-01")).not.toBeNull();
    expect(resolveTaxRate([base], "2025-12-31")).toBeNull();
  });

  it("breaks a tie the same way whatever case the ids are spelled in", () => {
    const bigger = { ...base, taxRateId: "0195B000-0000-7000-8000-000000000001" };
    const smaller = { ...base, taxRateId: "0195a000-0000-7000-8000-000000000001" };

    expect(resolveTaxRate([bigger, smaller], "2026-06-01")!.taxRateId).toBe(bigger.taxRateId);
    expect(resolveTaxRate([smaller, bigger], "2026-06-01")!.taxRateId).toBe(bigger.taxRateId);
  });

  it("does not care what order the candidates arrive in", () => {
    const newer = { ...base, taxRateId: "0195f000-0000-7000-8000-000000000002", effectiveFrom: "2026-06-01", percentage: "21.00" };

    expect(resolveTaxRate([base, newer], "2026-07-01")!.percentage).toBe("21.00");
    expect(resolveTaxRate([newer, base], "2026-07-01")!.percentage).toBe("21.00");
  });
});
