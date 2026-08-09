import DecimalJs from "decimal.js";

/**
 * The decimal constructor this module uses — **a clone, not the global one**.
 *
 * `decimal.js` keeps precision, rounding and exponential-notation thresholds as *global* mutable
 * state on the default constructor. Two things follow, and both matter for a rule that has to agree
 * with a server:
 *
 * - anything else in the bundle calling `Decimal.set(...)` would silently change how money rounds
 *   here (jsdom carries its own copy of this library, and a future chart or CSV export could
 *   reasonably reconfigure the shared one);
 * - the defaults are wrong for us anyway.
 *
 * A clone is isolated: this configuration cannot be changed from outside, and nothing here can
 * change anybody else's.
 *
 * **`precision: 34`** rather than the default 20 significant digits, because .NET's `decimal` carries
 * 28–29 and the parity vectors are the contract between them. Twenty is enough for any single
 * invoice line and not enough to promise that a chain of multiplications lands on the same digits.
 *
 * **`toExpNeg` / `toExpPos` pushed out of reach** so `toString()` never returns `1e+21`. The wire
 * form is safe either way — `toFixed` is fixed-point whatever the configuration — but the *raw*
 * amount is what a comparison against a vector's unrounded intermediate reads, and what a log line
 * explaining a disagreement prints. Exponential notation there is a difference in spelling that
 * reads as a difference in value.
 */
const Decimal = DecimalJs.clone({
  precision: 34,
  rounding: DecimalJs.ROUND_HALF_UP,
  toExpNeg: -9e15,
  toExpPos: 9e15,
});

export type DecimalValue = InstanceType<typeof Decimal>;

/**
 * How many decimal places a currency's minor unit has (ISO 4217).
 *
 * **The table is the exceptions, not the world**: two for almost everything, zero where a minor unit
 * does not exist, three for the dinars. A currency nobody has listed is treated as two, which is
 * right far more often than it is wrong.
 *
 * Deliberately not derived from `Intl.NumberFormat`: it answers a related but different question,
 * varies with the runtime's ICU version, and would make the phone's rounding depend on which
 * Android build it is running. This list is the same one `Money.MinorUnits` carries in C#, and the
 * two are meant to be compared by eye.
 */
const MINOR_UNITS: Readonly<Record<string, number>> = {
  BIF: 0, CLP: 0, DJF: 0, GNF: 0, ISK: 0, JPY: 0, KMF: 0, KRW: 0, PYG: 0,
  RWF: 0, UGX: 0, UYI: 0, VND: 0, VUV: 0, XAF: 0, XOF: 0, XPF: 0,

  BHD: 3, IQD: 3, JOD: 3, KWD: 3, LYD: 3, OMR: 3, TND: 3,
};

/** The default when a currency is not in the table above. */
const DEFAULT_MINOR_UNITS = 2;

export function minorUnitsOf(currency: string): number {
  return MINOR_UNITS[currency.toUpperCase()] ?? DEFAULT_MINOR_UNITS;
}

/**
 * A money amount in a single ISO-4217 currency — the device's mirror of `FieldKit.SharedKernel.Money`
 * (`BR-PRD-8`, `BR-PRD-9`).
 *
 * **Never a native `number`.** `0.1 + 0.2` is `0.30000000000000004` in IEEE-754, and a device that
 * priced an order that way would disagree with the server by a cent on lines nobody could predict.
 * `BR-PRD-8` requires an arbitrary-precision decimal, and the type system is what keeps it: there is
 * no constructor here that takes a `number`, so a float cannot get in by accident.
 *
 * **No implicit cross-currency arithmetic** (`BR-PRD-1`): adding EUR to USD throws rather than
 * inventing a rate.
 *
 * Immutable, like its C# counterpart — every operation returns a new value.
 */
export class Money {
  readonly amount: DecimalValue;
  readonly currency: string;

  private constructor(amount: DecimalValue, currency: string) {
    this.amount = amount;
    this.currency = currency;
  }

  /**
   * Reads an amount from a decimal **string**.
   *
   * A string and not a number, deliberately and at the type level: `Money.of(0.1 + 0.2)` is the
   * mistake this whole module exists to prevent, and a signature that accepts a `number` is an
   * invitation to make it. Strings are also what the API and the vector files carry, so this is the
   * shape the value actually arrives in.
   */
  static of(amount: string, currency: string): Money {
    const code = currency?.trim().toUpperCase() ?? "";

    if (!/^[A-Z]{3}$/.test(code)) {
      throw new Error(`Currency must be a 3-letter ISO-4217 code, got "${currency}".`);
    }

    // Garbage like "twelve" throws from decimal.js itself. What it *accepts* is the problem: "NaN"
    // and "Infinity" are legal arguments, and either would sail through every operation and reach
    // the wire as the literal string "NaN" — an amount no server will parse and no reader will
    // recognise as this line's fault.
    const parsed = new Decimal(amount);

    if (!parsed.isFinite()) throw new Error(`"${amount}" is not a decimal amount.`);

    return new Money(parsed, code);
  }

  static zero(currency: string): Money {
    return Money.of("0", currency);
  }

  /** How many decimal places this currency's minor unit has. */
  get minorUnits(): number {
    return minorUnitsOf(this.currency);
  }

  add(other: Money): Money {
    this.ensureSameCurrency(other);
    return new Money(this.amount.plus(other.amount), this.currency);
  }

  subtract(other: Money): Money {
    this.ensureSameCurrency(other);
    return new Money(this.amount.minus(other.amount), this.currency);
  }

  /**
   * Scales the amount — a discount percentage, a quantity, a tax rate.
   *
   * Takes a string or a `Decimal` for the same reason `of` does. A quantity is genuinely an integer,
   * and `multiply(3)` would be safe; allowing it would also allow `multiply(0.19)`, which is not.
   */
  multiply(factor: string | DecimalValue): Money {
    return new Money(this.amount.times(new Decimal(factor)), this.currency);
  }

  /**
   * Rounds half-up, away from zero, to the currency's minor units (`BR-PRD-9`).
   *
   * **Away from zero, not to even.** `2.125` is `2.13`, not `2.12`. Banker's rounding is what
   * `Math.round` in .NET does by default and what several JavaScript formatters do; a device
   * disagreeing with the server by a cent on a VAT line is a reconciliation someone chases through a
   * ledger.
   *
   * The scale defaults to the *currency's* minor units rather than to two: a yen has none, and
   * rounding 1234.5 JPY to 1234.50 invents a fraction of a unit no invoice can express. An explicit
   * scale is for a caller that genuinely means one.
   */
  round(decimals?: number): Money {
    const scale = decimals ?? this.minorUnits;

    return new Money(this.amount.toDecimalPlaces(scale, DecimalJs.ROUND_HALF_UP), this.currency);
  }

  /** Whether two amounts are the same money — same currency, same value. */
  equals(other: Money): boolean {
    return this.currency === other.currency && this.amount.equals(other.amount);
  }

  /**
   * The amount as the wire and the vectors carry it: a fixed-scale decimal string.
   *
   * `"19.00"`, not `"19"`. The scale is part of what a money amount says — a price list authored to
   * the cent should read back to the cent — and it is what makes a vector file comparable by string
   * equality rather than by a tolerance nobody can justify.
   */
  toWire(decimals?: number): string {
    return this.amount.toFixed(decimals ?? this.minorUnits);
  }

  toString(): string {
    return `${this.toWire()} ${this.currency}`;
  }

  private ensureSameCurrency(other: Money): void {
    if (this.currency !== other.currency) {
      throw new Error(
        `Cannot operate on different currencies: ${this.currency} vs ${other.currency}.`,
      );
    }
  }
}

/** A percentage as the API carries it — a decimal string like `"19.00"`, never a fraction. */
export function percentOf(amount: Money, percentage: string): Money {
  return amount.multiply(new Decimal(percentage).dividedBy(100));
}

export { Decimal };
