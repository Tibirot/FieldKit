# W11½ R6 — one answer to "what day is it"

**Status: decisions taken; R6a shipped, R6b next.** Both contract questions below were approved as
recommended — `OutletSnapshot` widens (Decision A), Order gets a new narrow contract returning the
**date** (Decision B), and an unrecognised zone **declines to answer** rather than falling back to
UTC.

| | | |
|---|---|---|
| **R6a** | the zone reaches the device — contract, feed, local store v20 | **shipped**, no behaviour change |
| **R6b** | both sides date pricing by it — `IOutletCalendar`, `RepriceAsync`, shared `businessDay`, vectors | next |

Closes regression [F6](regression-2026-08-13.md#f6--re-pricing-takes-the-capture-instant-as-a-utc-date).
Requirements: `BR-PRD-6`, `ORD-08`, `PRD-04`.

---

## 1. The defect

A price list runs by **calendar day** (`BR-PRD-6`), and a calendar day starts at a different instant
in every place. Two sides of this system currently answer "which day was this order priced for?"
with two different rules:

| | how it decides | file |
|---|---|---|
| device | the **rep's phone's local** day | `order.tsx:701`, `audit.tsx:1340` |
| server | the **UTC** day of the capture instant | `OrderIngestService.RepriceAsync` |

For a rep in Bucharest (UTC+2, or +3 in summer) working before 03:00, those are different dates. The
device prices against Tuesday, the server re-prices against Monday, and W11 slice 14's comparison
reports a disagreement the rep did nothing to cause — on the one screen whose entire purpose is to
flag orders where the rep and the back office differ.

Neither rule is right. The correct answer is **the outlet's** day: the shop is where the trade
happened, and it is the only party to the transaction that cannot move.

### What makes it worse than F6 originally said

F6 was written as "the server takes a UTC date". Reading the code for this write-up says the two
sides do not merely round the same instant differently — **they apply two different rules**. A fix
that only corrected the server would leave the disagreement intact.

---

## 2. Two facts that make this smaller than the plan estimated

**`Outlet.TimeZoneId` already exists, and is required.** It is an IANA zone, `HasMaxLength(64)`,
`IsRequired()`, validated on write, populated on every row. Its own doc comment, written in W1, says:

> A visit's business "day" and a promotion's validity both resolve here (BR-PRD-6), a rep may cross
> zones during a shift, and an offset would be wrong twice a year.

So the data is right and has been all along. **No migration, no new admin field, no backfill.** What
is missing is plumbing: the zone never leaves the Outlets module.

**The device already sends nothing about the day.** `PricingSnapshot` carries reference-data cursors,
not a date — so there is no "the device already told us, just trust it" shortcut available, and there
should not be. `RepriceAsync` exists to check the device independently; a server that adopted the
device's date would be checking the device against itself.

---

## 3. There are two day-rules here, and only one of them is wrong

This is the part most likely to be got wrong by a fix applied mechanically.

| function | whose day | correct today? |
|---|---|---|
| `todayOn(now)` in `lib/visits/today.ts` | the **rep's** local day | **yes — leave it alone** |
| `businessDay(now)` in `order.tsx` / `audit.tsx` | the rep's local day | **no — should be the outlet's** |

A journey plan is a fact about a rep's working day: `JRN-03` assigns calls to the days *they* work,
and a round that emptied at midnight in the shop's zone rather than the rep's would be wrong. The
unplanned call added in R4 uses `todayOn` for exactly this reason and must keep doing so.

Pricing is a fact about a shop's trading day. Only the second rule moves.

**So R6 does not collapse three functions into one.** It collapses the two duplicated `businessDay`
copies into one that takes a zone, and leaves `todayOn` alone with a comment saying why they are
different — because the next person to see two date functions will want to merge them.

---

## 4. The two decisions

### Decision A — `OutletSnapshot` grows `TimeZoneId`

A **public module contract change**, so it is escalated rather than assumed.

```csharp
public sealed record OutletSnapshot(
    Guid Id, string Code, string Name, Guid ChannelId, string? Segment, string Status,
    string? CountryCode, double? Latitude, double? Longitude, int RadiusMetres,
    string TimeZoneId,          // ← added
    long RowVersion);
```

**Why widen rather than add a contract.** This record's whole purpose is *what a device holds about
an outlet*. The device has to price with no signal, so it needs the zone locally; there is no second
delivery mechanism that would not be a second sync feed for one string.

**Blast radius:** three call sites, all inside Outlets and Sync — the two projections in
`ReferenceChangeFeed` (delta and baseline) and the pull payload in `PullEndpoints`. It rides the
existing outlets pull; no new endpoint.

**Wire compatibility:** a device on the old local-store version ignores an unknown property, and one
on the new version reads `null` until its next pull. Adding a property to a pull payload is
backwards-compatible in the direction that matters.

> **Approved and shipped as R6a.** One thing the implementation added that this section did not
> anticipate: **a delta pull would never deliver the zone to a shop nobody edits again.** Local store
> version 10 hit exactly this when `countryCode` arrived and answered it by dropping the outlets
> watermark so every row re-pulls; version 20 does the same. It also back-fills the held rows with
> `""` — which version 10 did not need, because *null* was already a meaningful `countryCode`. There
> is no meaningful empty zone, so writing one keeps `ReferenceOutlet` honest for the window between
> the upgrade and the next successful pull, and `""` is the one value the server can never send.

### Decision B — how the Order module learns an outlet's zone

Order has **no dependency on Outlets today**. Three options:

| | approach | verdict |
|---|---|---|
| B1 | a new narrow contract, e.g. `IOutletCalendar.BusinessDayAsync(outletId, instant)` | **recommended** |
| B2 | widen `OutletSummary` on `IOutletCatalog` | against that contract's stated rule |
| B3 | push the date decision into `IPricingService` | moves the problem into Products |

**B2 is ruled out by `IOutletCatalog`'s own documentation**, which says a caller needing more
"should ask for a contract that says what it needs, not for this one to grow" — and `IOutletClassification`
already exists as the precedent for answering that with a second narrow contract rather than a wider
one.

**B3 is worse than it looks.** `IPricingService.PriceAsync` takes a `DateOnly` deliberately and
"refuses to read a clock". Giving it an instant would make one module's rule depend on another's
calendar, and Products would need the zone anyway — the same gap, one module further away.

**B1 keeps the rule where the data is.** Outlets owns the zone, so Outlets answers "which business
day was this instant, at this shop". Order asks a question in its own vocabulary and never learns
what a time zone is.

Open sub-question for review: whether the contract returns the **date** (`BusinessDayAsync`) or the
**zone** (`ZonesOfAsync`). Returning the date keeps the conversion in one place and testable in one
place; returning the zone is more reusable and pushes the arithmetic to every caller. I lean to
returning the date, on the same "one implementation of a rule" argument this whole slice is about.

---

## 5. What the slice does, once those are settled

1. **Contract + feed** — Decision A, plus both projections in `ReferenceChangeFeed`.
2. **The new Outlets contract** — Decision B, with tests for DST boundaries.
3. **Device store version 20** — `ReferenceOutlet.timeZoneId`, defaulted for rows already held.
   Version 19 was the last (`taxTotal`, `capturedAgainst`).
4. **One `businessDay(now, timeZoneId)`** in `lib/pricing/`, replacing the two copies. Implemented
   with `Intl.DateTimeFormat` and an explicit `timeZone`, formatting parts rather than parsing a
   localised string.
5. **`RepriceAsync` asks for the day** instead of taking `CapturedAtUtc.UtcDateTime`.
6. **A vector file** — `vectors/pricing/business-day.v1.json`, read by both languages.

### Why a vector file, specifically here

This is the fifth rule implemented twice, and it is the one whose two implementations share **no
library at all**: .NET resolves zones through `TimeZoneInfo` and ICU; the device resolves them
through `Intl`. Agreement is not inherited from anything.

The cases worth pinning — and R7's lesson was that the *awkward* inputs are where engines diverge:

- Bucharest at 01:30 local (the reported defect), summer and winter.
- The instant a DST transition begins, in a zone that has one — both directions.
- A zone with a non-hour offset (`Asia/Kathmandu`, +05:45) — an implementation that stored an offset
  in minutes-per-hour fails here and nowhere else.
- A zone west of UTC (`America/Sao_Paulo`), so the sign is exercised in both directions.
- An unknown or malformed zone id — .NET throws `TimeZoneNotFoundException`, `Intl` throws
  `RangeError`, and the two must agree about what the *result* is. **This case needs a decision of
  its own** (see §6).

---

## 6. Known unknowns, listed rather than glossed

- **What happens when a zone id is not recognised.** .NET and V8 do not ship identical zone
  databases, and a tenant could hold a zone one runtime knows and the other does not. Falling back to
  UTC silently reintroduces the bug for that outlet. My inclination is that the device treats it the
  way it treats an unpriceable line — decline to answer, and let the order say *not re-priced* rather
  than *differs* — but this is a product decision.
- **Orders already captured** carry no zone and were priced by the rep's phone. Nothing re-prices
  them retroactively, so the fix is forward-only. That is the honest outcome and worth stating in the
  PR rather than discovering later.
- **`audit.tsx` uses `businessDay` too**, and whether an audit's date should follow the shop or the
  rep is a question this write-up has not answered. Pricing inside an audit follows the shop. If the
  *audit's own* date is a fact about the rep's day, it should use `todayOn` instead — which would be a
  behaviour change of its own and probably its own slice.

---

## 7. Size

The plan estimated 350 lines. That still looks right, distributed roughly as:

| | ~lines |
|---|---|
| contract + feed + pull | 40 |
| the new Outlets contract, with DST tests | 90 |
| device store v20 + migration test | 50 |
| shared `businessDay`, with tests | 60 |
| `RepriceAsync` + its tests | 40 |
| vector file + two readers | 90 |

That is over the ~400-line soft budget once docs are added, so it wants **stacking**: the contract
and feed first (small, reviewable, no behaviour change), then the two consumers.
