import { describe, expect, it } from "vitest";

import { Decimal, Money, minorUnitsOf, percentOf } from "@/lib/pricing/money";

/**
 * The device's money type, against the rules its C# counterpart follows (`BR-PRD-8`, `BR-PRD-9`).
 *
 * **These cases are `MoneyTests.cs`, deliberately.** Same currencies, same amounts, same expected
 * answers — a reader comparing the two files should see the same list. Where this file has more, it
 * is because JavaScript can go wrong in ways C# cannot: a float sneaking in, or `toString` deciding
 * to use exponential notation.
 */
describe("Money", () => {
  it("adds amounts in the same currency", () => {
    const sum = Money.of("10.50", "EUR").add(Money.of("4.25", "EUR"));

    expect(sum.toWire()).toBe("14.75");
    expect(sum.currency).toBe("EUR");
  });

  it("refuses to operate across currencies", () => {
    // BR-PRD-1. Adding EUR to USD has no answer without a rate, and inventing one silently is the
    // failure this refuses to make possible.
    expect(() => Money.of("10", "EUR").add(Money.of("10", "USD"))).toThrow(/different currencies/);
  });

  it("normalises the currency to upper case", () => {
    expect(Money.of("1", "eur").currency).toBe("EUR");
  });

  it.each(["", "EU", "EURO", "12X"])("refuses %s as a currency", (currency) => {
    expect(() => Money.of("1", currency)).toThrow(/ISO-4217/);
  });

  it("refuses an amount that is not a decimal", () => {
    // Two different refusals, and only one of them is ours. decimal.js throws on garbage like
    // "twelve" — but it *accepts* "NaN" and "Infinity", and either would pass every operation
    // silently and reach the wire as the literal string "NaN". That is the case worth a guard.
    expect(() => Money.of("twelve", "EUR")).toThrow();
    expect(() => Money.of("", "EUR")).toThrow();

    expect(() => Money.of("NaN", "EUR")).toThrow(/not a decimal/);
    expect(() => Money.of("Infinity", "EUR")).toThrow(/not a decimal/);
  });

  it("rounds half-up, away from zero", () => {
    // 2.125 -> 2.13, not 2.12. The one case that separates BR-PRD-9 from banker's rounding.
    expect(Money.of("2.125", "EUR").round(2).toWire()).toBe("2.13");

    // And symmetrically below zero — "away from zero" is not "up" for a negative.
    expect(Money.of("-2.125", "EUR").round(2).toWire()).toBe("-2.13");
  });

  it.each([
    ["EUR", 2],
    ["RON", 2],
    ["USD", 2],
    ["JPY", 0],
    ["KRW", 0],
    ["KWD", 3],
    ["BHD", 3],
    ["ZZZ", 2],
  ])("takes the minor units of %s from the currency", (currency, expected) => {
    expect(minorUnitsOf(currency)).toBe(expected);
    expect(Money.of("1", currency).minorUnits).toBe(expected);
  });

  it("rounds to the currency's minor units rather than to two", () => {
    // Rounding 1234.5 JPY to 1234.50 invents a fraction of a yen no invoice can express; truncating
    // 1.2345 KWD to 1.23 loses a fils.
    expect(Money.of("1234.5", "JPY").round().toWire()).toBe("1235");
    expect(Money.of("1.2345", "KWD").round().toWire()).toBe("1.235");

    // Unchanged for the currencies this project ships with.
    expect(Money.of("2.125", "EUR").round().toWire()).toBe("2.13");
    expect(Money.of("2.125", "RON").round().toWire()).toBe("2.13");
  });

  it("still takes an explicit scale when a caller means one", () => {
    expect(Money.of("1234.45", "JPY").round(1).toWire(1)).toBe("1234.5");
  });

  it("compares by value", () => {
    expect(Money.of("5", "EUR").equals(Money.of("5.00", "EUR"))).toBe(true);
    expect(Money.of("5", "EUR").equals(Money.of("5", "USD"))).toBe(false);
  });

  it("carries the scale on the wire, because the scale is part of what it says", () => {
    // "19" and "19.00" are the same number and different statements: a price authored to the cent
    // reads back to the cent, and string equality is what makes a vector file checkable.
    expect(Money.of("19", "EUR").toWire()).toBe("19.00");
    expect(Money.of("19", "JPY").toWire()).toBe("19");
    expect(Money.of("1.5", "KWD").toWire()).toBe("1.500");
  });

  it("never uses exponential notation, however large or small the amount", () => {
    // `toFixed` is fixed-point whatever the configuration, so the wire form is safe by construction.
    expect(Money.of("1000000000000000000000", "EUR").toWire()).toBe("1000000000000000000000.00");
    expect(Money.of("0.0000000001", "EUR").toWire(10)).toBe("0.0000000001");

    // The raw amount is the one that would say `1e+21` — and it is what a comparison against a
    // vector's unrounded intermediate, or a log line explaining a disagreement, actually reads.
    // This is what the cloned constructor's exponent thresholds are for.
    expect(Money.of("1000000000000000000000", "EUR").amount.toString()).toBe(
      "1000000000000000000000",
    );
    expect(Money.of("0.0000001", "EUR").amount.toString()).toBe("0.0000001");
  });

  it("keeps precision that a float would lose", () => {
    // The whole reason BR-PRD-8 exists. In IEEE-754 this sum is 0.30000000000000004.
    const sum = Money.of("0.1", "EUR").add(Money.of("0.2", "EUR"));

    expect(sum.toWire()).toBe("0.30");
    expect(sum.amount.equals(new Decimal("0.3"))).toBe(true);
  });

  it("multiplies without rounding until it is asked to", () => {
    // 12.99 * 19% is 2.4681 exactly. Rounding inside the multiplication would lose the digits the
    // rounding policy is supposed to see — BR-PRD-9 rounds the line, once.
    const tax = percentOf(Money.of("12.99", "EUR"), "19.00");

    expect(tax.amount.toString()).toBe("2.4681");
    expect(tax.round().toWire()).toBe("2.47");
  });

  it("agrees with the tax vectors' half-cent cases", () => {
    // The two cases `tax.v1.json` calls the most important in the file, checked here against the
    // money type itself rather than against a resolver that does not exist yet (slice 14).
    //
    // 1.00 * 4.5% = 0.045 — exactly half a cent. Half-up gives 0.05; banker's rounding gives 0.04.
    expect(percentOf(Money.of("1.00", "EUR"), "4.50").round().toWire()).toBe("0.05");

    // 3.00 * 4.5% = 0.135, where banker's rounding agrees by accident. A suite with only this case
    // would pass on a wrong implementation, which is why the file carries both.
    expect(percentOf(Money.of("3.00", "EUR"), "4.50").round().toWire()).toBe("0.14");
  });

  it("is immutable: an operation answers, it does not change the operand", () => {
    const price = Money.of("10.00", "EUR");

    price.add(Money.of("5.00", "EUR"));
    price.round(0);

    expect(price.toWire()).toBe("10.00");
  });

  it("does not let a float in through the door", () => {
    // Not a runtime assertion — a compile-time one, which is the point. `Money.of` takes a string,
    // so the mistake this module exists to prevent is a type error rather than a wrong answer.
    // @ts-expect-error a number is not an amount
    expect(() => Money.of(0.1 + 0.2, "EUR")).toBeDefined();
  });
});
