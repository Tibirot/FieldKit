# Cross-language test vectors

Shared inputs and expected outputs for the rules that must behave **identically in C# and
TypeScript** — pricing today, perfect-store scoring later ([testing strategy
§2](../docs/architecture/17-testing-strategy.md#2-unit-tests-many)).

These files are the contract between the two implementations. The C# engine runs them in
`FieldKit.Server.Tests`; the TypeScript device mirror runs the **same files** in W7. Neither owns
them, which is why they live here rather than inside either project.

> **Both languages read this directory now** (W7 slice 12). `price-resolution.v1.json` and its
> generated companion are executed by `PriceResolutionVectorTests.cs` and by
> `frontend/lib/pricing/price-resolver.test.ts`, from the same path — neither copies them. The
> TypeScript side also asserts the **format rule** these files depend on: every amount is checked to
> be a JSON *string* before any case runs, because `JSON.parse` would otherwise turn a bare number
> into a float before the engine saw it and the suite would be comparing two rounding errors. C#
> refuses the number token in its converter; TypeScript refuses it in a test. Same rule, enforced on
> both sides of the contract.
>
> **The mirror starts with money, not with a resolver** (W7 slice 11). Every rule in these files is
> arithmetic on amounts, so a resolver written on top of a float would fail them for reasons that
> have nothing to do with the rule it was testing. `frontend/lib/pricing/money.ts` is the
> `decimal.js` counterpart of `SharedKernel/Money.cs` — same minor-units table, same half-up policy —
> and its tests are deliberately the same cases as `MoneyTests.cs`, so the two files can be compared
> by eye. The resolvers that consume the vector files land in slices 12–14.

## Why a file rather than a shared test suite

The two engines cannot share code — one is .NET on a server, the other is TypeScript on a phone
(`PRD-08`). What they can share is *agreement about answers*. A vector file is the smallest thing
that expresses that: no build coupling, no generated bindings, readable in a diff when someone
changes a rule.

It also makes disagreement legible. If a case fails in TypeScript and passes in C#, the failing case
names itself.

## Format rules

**Money is a string, never a JSON number.** `"12.50"`, not `12.50`. This is the same rule as the wire
format (`BR-PRD-8`, [api-contracts §1](../docs/architecture/13-api-contracts.md#1-shape--conventions))
and it matters more here than anywhere: a vector file exists to prove decimal behaviour, and
`JSON.parse` would turn a bare number into a float **before the engine under test ever saw it**. The
suite would then be checking that both sides make the same rounding error.

**Dates are `YYYY-MM-DD` strings.** No timestamps — these are business days, and a business day
starts at different instants in different places (`BR-PRD-6`).

**Enums are names.** `"Outlet"`, not `1`. The ordinal is storage, not interchange, and a file that
outlives a member being inserted should still mean what it said.

**`expected: null` is a case, not a gap.** "No price applies" is an answer the engine has to give,
and a file that only carried positive cases would let a resolver that always returns *something*
pass.

**Every file carries a `version`.** A change that alters what a case means bumps it, so a mirror
running an older file fails loudly rather than silently testing yesterday's rules.

## The harness (W7 slice 15)

Both engines running the same files is the mechanism; a **CI job named after it** is what makes the
mechanism a guarantee. `parity (C# ↔ TypeScript vectors)` runs the C# vector suites and the
TypeScript ones and fails if either does — so "this rule used to hold in both languages and now holds
in one" has a check of its own rather than showing up as an ordinary test failure in whichever job
happened to notice.

Three things it does that neither suite can do alone:

- **Every vector file is read by both languages** (`scripts/check-vector-readers.mjs`). A file added
  with one reader, or a reader quietly deleted, leaves a rule proven on one side and unchecked on
  the other while every job stays green. This is the only assertion here that no test can make about
  itself: C# passing says nothing about what TypeScript read.
- **Neither side may quietly run nothing.** `dotnet test --filter` exits 0 when nothing matches, and
  a vector file that failed to load leaves a green suite of whatever remained. Both steps assert a
  floor on the number of cases that actually ran.
- **The counts are deliberately unequal.** C# runs ~79 cases and TypeScript ~800, because the
  generated files are an *oracle*: their expectations came from the C# engine, so running them back
  against it would confirm a bug rather than find one. Anyone reading the job output should expect
  the asymmetry rather than treat it as a gap.

## Ordering ids — read this before implementing a tiebreak

Every engine here breaks a tie on the **higher id, compared as big-endian bytes**, which is the order
the canonical string form prints. Implement it as an ordinal comparison of the lowercase canonical
strings, or as a comparison of `ToByteArray(bigEndian: true)`. Both give the same answer.

**The trap is `Guid.ToByteArray()` with no argument.** It returns the first three groups
*little-endian*, so comparing those bytes orders `00000100-…` **below** `00000002-…` — backwards
from what the strings say. Each hand-written file carries that exact pair.

> **A correction, kept here because the wrong version shipped.** Slices 6–9 justified the explicit
> byte comparison by claiming .NET's `Guid.CompareTo` reads the first field as a *signed* int and so
> sorts `ffffffff-…` below `00000001-…`. **That is false** — it compares those fields unsigned, and
> agrees with byte order. The "hostile" pair chosen to catch it therefore discriminated nothing, and
> three files carried a case whose stated purpose it did not serve.
>
> Slice 10's mutation testing found it: reverting a resolver to `Guid.CompareTo` broke no test, which
> it should have. The pairs are now `00000100` / `00000002`, which do discriminate — against the
> naive byte array, the mistake that actually exists.
>
> The implementation never changed, and the underlying reason held up: the mirror is TypeScript and
> has no `Guid` type, so the ordering has to be *specified* rather than inherited from a platform.
> What was wrong was the example, not the rule.

## Files

| File | Covers | Consumed by |
|---|---|---|
| [`pricing/price-resolution.v1.json`](pricing/price-resolution.v1.json) | `PRD-04` / `BR-PRD-2` — which price list wins for an outlet on a date | `PriceResolutionVectorTests` (C#); the device mirror (W7) |
| [`pricing/promotion-resolution.v1.json`](pricing/promotion-resolution.v1.json) | `PRD-06` / `BR-PRD-3` — which promotion applies to one line, at a quantity, on a date | `PromotionResolutionVectorTests` (C#); the device mirror (W7) |
| [`pricing/tax.v1.json`](pricing/tax.v1.json) | `PRD-07` / `BR-PRD-5`, `BR-PRD-9` — which rate applies, and what it does to a line | `TaxVectorTests` (C#); the device mirror (W7) |
| [`pricing/price-resolution.generated.v1.json`](pricing/price-resolution.generated.v1.json) | the same rules, swept rather than chosen | `GeneratedVectorTests` (C#); the device mirror (W7) |
| [`pricing/promotion-resolution.generated.v1.json`](pricing/promotion-resolution.generated.v1.json) | ditto | `GeneratedVectorTests` (C#); the device mirror (W7) |
| [`pricing/tax-application.generated.v1.json`](pricing/tax-application.generated.v1.json) | ditto | `GeneratedVectorTests` (C#); the device mirror (W7) |

**A mirror consumes all six.** The generated three are the same format and the same reader — that is
the whole point of having settled the format against real engine code — so "read the vectors" means
this table, not the hand-written half of it.

The second and third files arrived without changing the format, which is the point of having decided
it against real engine code rather than at the end. Each new rule got a new case file, not a new
convention, and the mirror learns one reader for all of them.

**The tax file is the one this whole apparatus was built for.** The other two decide *which* record
wins, and a mirror that got the comparison wrong would return a visibly different id. Tax does
arithmetic, and a mirror that got the rounding wrong returns a number one cent away — which nobody
notices until a ledger does. Its `application` cases are paired deliberately: for every half-cent
case where half-up and banker's rounding disagree, there is one where they agree by accident, so a
suite cannot pass on the wrong policy.

## Hand-written and generated, side by side

The `*.v1.json` cases are written by hand, one per rule, each named after the rule it pins. That is
enough to make the format real and to hold the C# engine to it.

It is **not** enough for `BR-PRD-8/9`. The testing strategy asks for *generated / property-based*
vectors precisely because hand-written cases cover the regions someone thought of, and decimal
divergence hides in the ones they did not.

**The generated suite landed with W6 slice 10**, in the `*.generated.v1.json` files above. It emits
into this same format, so the mirror needs one reader rather than two. `VectorGenerator` produces
them from a fixed seed and `GeneratedVectorTests` checks the committed artifacts against a fresh run
— a regenerated file that differs is either a rule change or a bug, and either way it shows up as a
diff rather than as a silent pass.

Neither kind replaces the other. The hand-written cases say *why* a rule exists and are readable as
documentation; the generated ones say the engine holds across a range nobody would type out.
