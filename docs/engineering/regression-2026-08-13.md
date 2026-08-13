# Full regression — 13 August 2026, after W11

A whole-app pass at the end of Week 11, run against `main` at
[`16d52ca`](https://github.com/Tibirot/FieldKit/commit/16d52ca) — every automated gate, plus static
analysis and a browser sweep looking for the things a green suite cannot tell you.

**Headline: every gate is green and nothing found is broken in what shipped.** The findings are seven
gaps — a doc that has drifted, a stored value nothing reads, a rule mirrored in two languages with no
shared corpus, three latent test flakes, a small usability limit, and one `Must` whose device half is
absent. They are ordered by what they would cost if left.

---

## 1. What was run

| Gate | Result |
|---|---|
| `dotnet test` (unit + architecture + Testcontainers integration) | **1234 passed**, 0 failed |
| — `FieldKit.Server.Tests` | 1149 |
| — `FieldKit.Infrastructure.Tests` | 19 |
| — `FieldKit.ArchitectureTests` | 12 |
| — `FieldKit.SharedKernel.Tests` | 54 |
| `npm run lint` (eslint) | clean |
| `npx vitest run` | **2726 passed**, 97 files |
| `npx next build` (production) | succeeded |
| `check-deploy-manifest.mjs` | OK |
| `check-vector-readers.mjs` | 12 vector files, each read by both languages |
| Every route reachable | 22/22 → 200 |
| Browser sweep, signed in as admin | no React errors, no hydration warnings |

The architecture gates are included in that run: the module-boundary tests, the `IgnoreQueryFilters`
ban and the `DateTimeOffset.UtcNow` ban all hold.

The browser sweep ran twice — once as an administrator over the back office, once as a rep over the
field app. §2a records what the rep-side pass covered and what it could not reach.

---

## 2a. The rep-side sweep

Signed in as the rep, on a device holding four outlets and seven visits from earlier work.

**Exercised and correct:**

| Surface | Result |
|---|---|
| Today's journey, no plan for today | correct empty state, and it says what to do next |
| Sync indicator | live, and honest — reported the two photographs this device is genuinely still holding |
| Offline → online | chip switches to *"Offline — your work is saved on this device"* and recovers on reconnect |
| This device screen | shops held, waiting-to-send, last-synced and storage all correct |
| `/en/offline` route | 200 |
| Console across every rep screen | no errors, no React warnings, no hydration warnings |

**Three things that look like defects and are not.** Recorded so nobody spends an afternoon on them:

- **"Storage used 0 of 20358 MB"** while the device holds photographs. `navigator.storage.estimate()`
  reports 235,601 bytes; whole megabytes is a deliberate choice with a comment saying so
  (*"a rep comparing 18 with 2048 does not need either to three decimals"*). Accurate, not a bug.
- **No service worker registered.** `ServiceWorkerRegistrar` skips registration outside production
  because `public/sw.js` is a post-build artefact, and the code explains that registering under
  `next dev` would log a 404 on every page load. Deliberate — but see the coverage limit below.
- **Every shop reports "No steps are set up for this shop."** The four outlets this rep holds are in a
  channel with no visit workflow. Configuration, not code, and the empty state is handled gracefully.

**What this pass could not reach, and why:**

- **Check-in, visit steps, the audit screen and order capture.** All four sit behind a planned call,
  and this rep has none for today. Their behaviour rests on unit and integration tests plus the manual
  walks during W11 slices 9–14. Getting to them needs either a published plan for today or the
  unplanned path in **F7** below — which is itself the finding.
- **True offline shell loading.** The service worker only exists after a production build, so
  `next dev` cannot demonstrate the app starting with the network gone. Exercising it needs
  `npm run build && npm start`.

## 2. Findings

### F1 — `errorCode` is stored on every rejected mutation and read by nothing

**Where:** `frontend/lib/sync/outbox.ts:126`, `frontend/lib/sync/db.ts:835`

`markRejected` stores `errorCode` and `errorDetail` on the outbox entry, under a comment that says:

> The code is an `ADR-0012` string **the UI translates**; the detail is the server's English, shown
> only where no translation exists.

Nothing translates it. `errorCode` and `errorDetail` have exactly six references in the whole front
end — the type declaration, the parameters, and the two assignments. No screen reads either.

A rep whose order the server refused sees **"Needs attention"** and cannot find out why, on the one
surface where the reason matters most: the work is already done and only a person can unstick it.

The machinery to fix it already exists. `refusalText` in `frontend/lib/api/refusals.ts` translates a
code with an English fallback, and is used on the back-office HTTP path. The offline path stores the
same codes and is simply not wired to it.

`ADR-0012` predicted this shape exactly: *"the parity test covers catalogue-to-catalogue drift; it
does not cover server-to-catalogue drift. Closing that needs a test that walks the codes the server
can emit."* 44 of the 51 refusal codes the modules emit appear nowhere in the front end — most
legitimately, because they surface through `refusalText`'s fallback, but the offline path has no
fallback at all.

**Cost if left:** `OFF-09`'s promise — refused work is visible and explains itself — is half-built.
This is the same shape as `lastFailure` before W11 slice 12c, where a swallowed reason hid a broken
feature for a whole slice.

**Fixed in W11½ R5**, as wiring rather than new machinery: a `refusalOf` reader, a `storedRefusalText`
translator beside `refusalText`, and a `RefusedReason` beside every `SyncBadge`. The catalogue did not
have to grow — ADR-0012's English fallback carries every push-time code.

**One thing the fix had to discover.** `refusalText` is safe because it passes the server's `args`
through; `markRejected` never stored any, and `t.has` cannot tell an entry with placeholders from one
without. **`next-intl` does not throw on a missing ICU value** — it reports the error and returns the
key path — so the obvious guard is not one, and a rep would have been shown
`Refusals.journey.plan.windowTooLong`, which is precisely the failure ADR-0012 exists to prevent. The
template is inspected for a brace instead.

This is ADR-0012 stage 4's gap seen from a new angle: the ADR asked for a test walking the codes the
server can emit, and the reason it matters is not only that a code may be missing from the catalogue
— it is that a code *present* in the catalogue can be unrenderable from what the device kept.

**Fix:** wire `refusalText` into the badge or the visit/order screen. Small; the data and the
translator both exist.

---

### F2 — `IOrderMinimumChangeFeed` is missing from the module registry

**Where:** `docs/architecture/10-module-boundaries.md` §7

Checked mechanically: every `public interface I…` in a `*.Contracts` project against the registry
table. 32 of 33 are listed. `IOrderMinimumChangeFeed` is not.

`CLAUDE.md` makes the registry a deliverable that moves with the code, so this is a doc defect rather
than a nit — the registry is the map people trust for what a module publishes.

**Fix:** one table row. Trivial, and worth doing before the registry stops being trustworthy.

---

### F3 — The order-minimum rule is mirrored in two languages with no shared corpus

**Where:** `FieldKit.Modules.Products/OrderMinimumResolver.cs`, `frontend/lib/pricing/order-minimum.ts`

`vectors/` holds 12 shared files covering line pricing, price resolution, promotion resolution, tax,
the perfect-store score, geofencing and the push wire. The order minimum has none, and it is resolved
independently on both sides.

`BR-ORD-5` is explicit that this is the *only* business rule in the module with no server-side gate —
the device is where it can still be acted on. That makes the two implementations agreeing more
important than usual, not less: nothing downstream will catch a divergence, because nothing
downstream checks.

This was noticed during W11 and recorded in passing; it is written down here so it stops being
folklore.

**Cost if left:** a rep is told an order meets the minimum and the back office believes otherwise, or
the reverse — with no test in either language that could fail.

**Fix:** a `vectors/pricing/order-minimum.v1.json` and a reader on each side, matching the existing
five.

**Fixed in W11½ R7 — and the file found a real divergence on its first run.**

The two engines agreed about everything the rule is *about*: precedence, ties, the comparison, the
currency refusal. They disagreed about **what counts as a number**. `OrderMinimumResolver.Check`
parses with `NumberStyles.AllowDecimalPoint | AllowLeadingSign`, which excludes exponents and
hexadecimal; `decimal.js` reads `"1e2"` as 100 and `"0x10"` as 16. A device would have reported an
order **Met** against a minimum the server cannot read at all — and since `BR-ORD-5` has no
server-side gate, nothing downstream would ever have said so.

**It is unreachable today.** `OrderMinimumEndpoints` validates with the identical styles, so no such
amount can be stored. That is the point rather than a mitigation: the agreement was inherited from
two validators that happen to match, and nothing recorded it. The device now refuses the same shapes
explicitly and takes the stricter side — `Unreadable` stops a submission, `Met` lets one through.

**Worth carrying forward:** every hand-written case about the rule itself passed on both sides
first time. What diverged was the handling of input the rule was never meant to receive.

---

### F4 — Three latent test flakes of a shape that already cost a CI run

**Where:**
- `frontend/components/field/audit.test.tsx:627`
- `frontend/components/field/audit.test.tsx:751`
- `frontend/components/field/order-minimum.test.tsx:220`

All three do this:

```ts
await waitFor(async () => expect(await db.outbox.count()).toBe(1));
expect(screen.queryByText(/…/)).toBeNull();          // ← unwaited
```

The store write and the DOM update are two different moments. Waiting for the **store** and then
asserting the **DOM** leaves a gap that a live query fills asynchronously — so the assertion can run
while the element is still on screen and fail.

This is not hypothetical. The same shape in `audit.test.tsx` failed CI during W11 slice 14 on a change
that touched none of it, and was fixed there by waiting for the DOM instead. It is also the third
occurrence overall: W11 slice 9c hit it and got away with it because `userEvent.click` happened to
flush the query.

The comment above line 627 is the giveaway — it records that the test *"went red about one run in
three"* and lengthens the **store** wait to fix it, which addresses the wrong half.

**Fix:** wrap each DOM assertion in `waitFor`. Three lines. Worth considering a shared helper or a
lint rule at this point, since the pattern has now been rediscovered three times.

---

### F5 — A picker cannot distinguish two people with the same name

**Where:** `frontend/components/back-office/journey-plans.tsx:176`

The rep picker renders `candidate.displayName` and nothing else. `UserResponse` already carries
`Email` and `SubjectId` on the same payload.

**How it was found, honestly:** the dev database contains eight *distinct* users all displaying
"Maria Ionescu", which is an artifact of running the `.http` create-user request repeatedly over the
project's history — **not a defect, and not reported as one.** But it made the real limitation
obvious: a supervisor picking a rep sees eight identical rows and no way to choose. Real tenants have
people who share a name.

**Fix:** render the email as secondary text. The data is already on the wire.

**Fixed in W11½ R3**, in three places rather than one — `assignment-form.tsx:159` and
`working-calendars.tsx:193` carry the same line, and this finding only looked at the journey screen.
"Secondary text" turned out not to be available: an `<option>` holds no elements, so the label is
`name — email` as a single string from a shared `identifying` helper.

---

### F6 — Re-pricing takes the capture instant as a UTC date

**Where:** `FieldKit.Modules.Order/OrderIngestService.cs` (`RepriceAsync`)

Shipped knowingly in W11 slice 14 and flagged in its PR; repeated here so it is in one list with
everything else. A price list runs by calendar day, and an outlet in Bucharest changes day six hours
before one in London (`BR-PRD-6`). An order taken at 01:30 local resolves against the previous UTC
day, and would re-price against the wrong side of a price change that happened overnight.

The outlet's time zone is not on the snapshot the Order module can see. This is the **same gap**
already recorded against `OutletSnapshot`, and the two should be fixed together rather than
separately.

**Cost if left:** a narrow window of false disagreements, on orders taken late at night in a tenant
that changed prices that day.

> **Sharpened while planning the fix, and it is worse than the paragraph above says.** The device does
> not use UTC — `businessDay` in `order.tsx:701` and `audit.tsx:1340` reads `getFullYear`,
> `getMonth` and `getDate`, which are **local**. So the two sides do not merely round the same instant
> differently; they apply two different rules. A rep in Bucharest before 03:00 has a device that says
> one day and a server that says the day before, and W11 slice 14's comparison will flag the result as
> a disagreement the rep did nothing to cause.
>
> That moves the fix from "use the outlet's zone server-side" to "**both sides date pricing by the
> outlet's zone**", which is `W11½` slice **R6**. `businessDay` is also duplicated across the two
> screens, and collapses into one function that takes a zone.

---

### F7 — A rep cannot add an unplanned visit, and `JRN-06` is a Must

**Where:** the field app has no writer for it. `frontend/lib/visits/` exports `checkIn`, `checkOut`,
`completeStep` and `markNotVisited` — and nothing for an unplanned call.

`JRN-06` — *"Not-visited with reason; **add unplanned visit**"* — is a **Must**, Phase 2. Half of it
is built and reachable: `NotVisited` renders from the check-in screen whenever there is a planned
visit id. The other half exists everywhere **except** the place that would create one:

| Layer | Unplanned call |
|---|---|
| `JourneyIngestService.AddUnplannedAsync` | built |
| `JourneyPlan.TryAddUnplanned`, `VisitSource.Unplanned` | built |
| `/sync/push` wire slot `unplanned` | built |
| Sync manager: `"UnplannedCall" → "unplanned"` | built |
| Back office renders the *Unplanned* badge | built |
| **Anything on the device that enqueues one** | **missing** |

The only mention of `UnplannedCall` in the whole front end is the slot mapping in
`frontend/lib/sync/manager.ts:250` — a route for a mutation the device cannot produce.

**Cost if left:** a rep standing in a shop that is not on today's plan cannot record the call at all.
Worse, it is the *only* way into the field app when a plan is missing: with no planned calls, this
rep's app offers **Sync now** and **This device** and nothing else. That is the state the sweep found
it in, and it is why check-in, the audit and order capture could not be exercised by hand.

The journey spec anticipated this shape — *"an unplanned call belongs to no cycle and so cannot be
moved at all. That is not an omission"* — so the design question is settled; only the device's entry
point is absent.

**It does not read as a deliberate deferral.** W7 slice 5 is named *"Rep-side annotations —
not-visited with reason, **unplanned visit**, reschedule within cycle"*, and the delivery plan carries
no note saying the device half was dropped — the row has no outcome annotation at all. So the most
likely reading is that the server half shipped under that slice, the device half followed for
not-visited only, and nothing noticed the other one was still missing.

That is worth checking against intent before scheduling, since the plan is the authority on what
actually shipped. If it was deferred, the row should say so.

**Fixed in W11½ R4.** It was not a deferral: `JRN-06` is a Must and the plan's row carried no note,
so the device half was simply never built. The slice adds `lib/visits/unplanned.ts` beside
`markNotVisited`, a collapsible picker under the round, and the enqueue at check-in. Reading the code
to build it turned up **F8** below, which the sweep could not have seen from the outside.

---

### F8 — A second call at a worked shop is recorded against the call it is not

**Where:** `frontend/components/field/todays-journey.tsx:150` (`destinationOf`)

Found while building R4, not during the sweep. The function's own doc comment says:

> A *finished* visit still goes to check-in, and that is deliberate rather than an omission: the
> sealed visit is a record, and what a rep at that shop wants next is an unplanned second call
> (`JRN-06`), not the read-only page.

The routing agrees with the first half and contradicts the second: the link is
`/field/outlets/{outletId}?call={plannedVisitId}` for **every** stop, worked or not. So the second
call carries the planned call id, is captured as that call rather than as an unplanned one, and — as
of R4 — is the one path that reaches check-in without queuing an `UnplannedCall`.

**Cost if left:** a rep who calls twice at one shop has the second call recorded as the first. The
coverage figure is unaffected (the call was already counted), so this understates activity rather
than overstating it — which is why it is a finding rather than a defect worth stopping R4 for.

**Fix:** drop the `call` parameter when `stop.progress === "worked"`. Not `notVisited` — a shop that
opened after all *is* the planned call being made, and carrying the id is what lets the round agree.

Left out of R4 deliberately: it is a behaviour change to the planned path, with its own tests, in a
slice that is already at the top of its budget.

---

### F9 — An unplanned call still needs a published round covering the day

**Where:** `JourneyIngestService.AddUnplannedAsync` — `JourneyIngestRefusal.NoPlanForDate`

Found by walking R4 in a browser, as the dev rep, on a day their plan does not cover. The device
queued the call, pushed it, and the server refused it:

```
journey.plan.noneForDate — "You have no published round covering that day."
```

The server is right: an unplanned call is a row *on a plan*, and there is no plan to put it on. But
it means **F7's headline claim was only half true**. F7 said the missing entry point was "the only way
into the field app when a plan is missing"; R4 delivers that half — check-in, the audit and order
capture are all now reachable with no round — while the journey annotation is refused until a
published plan covers the day.

**Cost as it stands:** the rep's *visit* is captured and reaches the back office normally. What they
lose is the call appearing on a round, and — because **F1** is still open — the badge says *Needs
attention* with no reason, while the outbox holds `errorCode: "journey.plan.noneForDate"` and the
sentence above, unread. This is F1's exact shape, observed live rather than reasoned about.

**Not obviously a defect.** Three readings, and the choice belongs with the journey spec rather than
with a slice:

1. *Correct as designed* — coverage is measured against a plan, and a call outside every plan is not
   a fact about anybody's round. Then R5 is the whole fix: say why.
2. *The device should not offer what the server will refuse* — the picker could hide the section when
   no round covers today. Cheap, and it would have hidden the entry point in exactly the state that
   motivated the slice.
3. *An unplanned call should create the round it needs.* The largest change and the only one that
   makes "a call you can start anywhere" literally true.

**Recommended:** ship **R5** first — it turns this from a silent failure into a sentence a rep can act
on — and take the question to the journey spec afterwards.

---

## 3. Debts confirmed still open

Carried from earlier slices, re-checked and still true. None is a defect; each is a decision with a
stated cost.

| Debt | Recorded | Still open |
|---|---|---|
| `OutletSnapshot` carries no `timeZoneId` | W11 | yes — see F6 |
| Photo retention / pruning (`OFF-11`) | W11 slice 12b | yes |
| `Photo`-type survey questions cannot be answered | W10 | yes |
| `measured()` and `Audit.Check` share no evidence | W11 slice 11 | yes |
| Form-per-channel survey configuration | W10 | yes |
| `ORD-04`, `ORD-13`, `ORD-15` deferred out of W11 | W11 plan | yes, by plan |

Closed since they were recorded: the captured order's missing tax field (slice 14), and `confirm` +
the missing-blob rule (slices 13a/13b).

---

## 4. What this pass says about the test suites

Worth stating, because it is the thread running through W11 and through these findings.

**Every defect found this week lived in a gap between two green suites**, not inside one:

- The CSP and CORS walls (12c) — device tests mock `fetch`; server tests `PUT` from .NET where there
  is no browser.
- The container-app manifest (12d) — 1215 tests passed; only the deploy artifact knew.
- The batching bug in the confirm client (13b) — caught while writing it, by reasoning about the
  reply shape, not by a test.
- F1 above — both sides are individually correct; nothing tests that a stored code is ever displayed.

The pattern is that **the suites test each side of a seam and nothing tests the seam**. The parity
vectors are this codebase's answer to that problem and they work well — F3 is a case where the answer
exists and has not been applied.

---

## 5. Suggested order

1. **F7** — confirm against intent first; if it is not a deferral it is an unshipped `Must`, and it is
   the reason a rep with no plan can do nothing at all.
2. **F4** — three lines, removes a known CI flake.
3. **F2** — one table row.
4. **F1** — the finding that costs a rep most today, and the machinery to fix it already exists.
5. **F3** — a vector file and two readers; prevents a silent divergence.
6. **F5** — one line of JSX.
7. **F6** — with the `OutletSnapshot` time-zone debt, not before.

None blocks W12 on its own. **F7 is the one that would change the plan**, because it is scope rather
than polish — and because W12's demo is the full loop, which a rep who cannot start a call cannot
walk.

> **Planned as `Week 11½` in the [delivery plan](../delivery-plan.md).** Seven slices, R1–R7, in the
> order below. Two decisions were taken when it was written: F7 is **built** rather than deferred
> (`JRN-06` is a Phase-2 Must and the server half exists), and F6 is fixed on **both sides** through
> the outlet's own time zone rather than server-side alone.

Two things worth doing regardless of the list, both about the *next* pass rather than this one:

- **A published plan for today in the dev seed.** Half of what this sweep could not reach was
  unreachable for want of one, and the same will be true of every future manual pass and of the W12
  demo.
- **A production build in the loop.** `next dev` cannot register the service worker, so the offline
  shell — the app's central claim — is the one thing no local check ever exercises.
