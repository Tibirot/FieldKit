# Cross-language test vectors

Shared inputs and expected outputs for the rules that must behave **identically in C# and
TypeScript** — pricing today, perfect-store scoring later ([testing strategy
§2](../docs/architecture/17-testing-strategy.md#2-unit-tests-many)).

These files are the contract between the two implementations. The C# engine runs them in
`FieldKit.Server.Tests`; the TypeScript device mirror runs the **same files** in W7. Neither owns
them, which is why they live here rather than inside either project.

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

## Files

| File | Covers | Consumed by |
|---|---|---|
| [`pricing/price-resolution.v1.json`](pricing/price-resolution.v1.json) | `PRD-04` / `BR-PRD-2` — which price list wins for an outlet on a date | `PriceResolutionVectorTests` (C#); the device mirror (W7) |
| [`pricing/promotion-resolution.v1.json`](pricing/promotion-resolution.v1.json) | `PRD-06` / `BR-PRD-3` — which promotion applies to one line, at a quantity, on a date | `PromotionResolutionVectorTests` (C#); the device mirror (W7) |

The second file arrived without changing the format, which is the point of having decided it against
real engine code rather than at the end. A new rule got a new case file, not a new convention, and
the mirror learns one reader for both.

## Hand-written today, generated later

These cases are written by hand, one per rule, each named after the rule it pins. That is enough to
make the format real and to hold the C# engine to it.

It is **not** enough for `BR-PRD-8/9`. The testing strategy asks for *generated / property-based*
vectors precisely because hand-written cases cover the regions someone thought of, and decimal
divergence hides in the ones they did not. The generated suite is W6 slice 10; it will emit into this
same format, so the mirror does not have to change to consume it.
