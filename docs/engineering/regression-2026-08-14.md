# Full regression — 14 August 2026, after Week 11½

A whole-app pass after the seven remediation slices of [Week 11½](../delivery-plan.md) landed. It
checks that those fixes hold, and looks for what they did not cover.

Its predecessor is [regression-2026-08-13.md](regression-2026-08-13.md), which found F1–F9 and
produced W11½. **That file's findings are all closed or recorded**; §4 below says which and how.

> **The headline.** Every gate is green and the whole rep loop now works end to end in a browser —
> which it did not a day ago. The five findings are all the *same shape*: something built, reachable
> only through a door nobody opened, or answered into a field nobody reads. That shape has now
> appeared nine times across two sweeps, and §6 argues it deserves a check of its own rather than
> another list.

---

## 1. What was run

| Gate | Result |
|---|---|
| `dotnet test` (unit + architecture + Testcontainers) | **1,279 passed** |
| `npm test` (frontend) | **2,811 passed** |
| `npm run lint` | clean |
| `npm run build` (production + service worker) | clean — 73 files precached |
| `scripts/check-vector-readers.mjs` | **14** shared vector files, each read by both languages |
| `scripts/check-deploy-manifest.mjs` | manifest OK against a real `--publisher manifest` run |
| Architecture tests, incl. W11½ R1's registry gate | 14 passed |

**Walked by hand**, signed in as the dev rep, against the real server:

- the journey screen with no round → **the unplanned-call entry point** (R4)
- shop picker → check-in with a geofence override
- **order capture**: catalogue, a line at 6 × 3.40 EUR, tax at 19%, total 24.28 EUR (R6b)
- submit → check-out → sync
- the push: `CapturedVisit` and `CapturedOrder` accepted, `UnplannedCall` refused
- **the refusal rendered on the picker row** with the server's own sentence (R5)

Two things that could not be reached, and why, are in §5.

---

## 2. Findings

### F1 — Order and audit capture are reachable only through a workflow step

**Where:** `frontend/components/field/visit.tsx:225` and `:238`

Both links are gated on a step:

```tsx
{open && step.type === "Order" ? <LinkButton href={`…/order`}> … }
{open && step.type === "Audit" ? <LinkButton href={`…/audit`}> … }
```

A channel with **no visit workflow** therefore has no way to take an order or run an audit. That is
not a broken screen: navigating straight to `/field/visits/{id}/order` in this sweep produced a fully
working order screen, took a line, priced it and submitted it. **Only the door is missing.**

**A channel with no workflow is a supported state, not a misconfiguration.** `check-in.tsx` says so
explicitly — "a channel with no workflow held means presence expected rather than no opinion" — and
the visit screen has a sentence for it: *"No steps are set up for this shop. Do the call and check out
when you are done."* The app tells the rep to do the call and then offers them nothing to do.

**Cost if left:** `ORD-01` and `AUD-01` are both **Musts**, and both are reachable only via optional
configuration. A tenant that has not authored a workflow for a channel cannot take an order in it.

**It is also why two consecutive regressions could not test order capture by hand.** The previous
sweep blamed the missing unplanned-visit entry point (F7); R4 fixed that, and this sweep got one step
further and hit the same wall from the other side.

**Fix:** the visit screen should offer both regardless of steps — the step, where present, is what
`BR-VIS-3` gates *completion* on, not what makes the screen reachable. Worth pairing with a decision
about what a call with no configured workflow is *for*.

---

### F2 — `RescheduledCall` has no device writer, and `JRN-06` names it

**Where:** the field app has no writer. `frontend/lib/visits/` exports `checkIn`, `checkOut`,
`completeStep`, `markNotVisited` and now `addUnplanned` — and nothing for a reschedule.

Every other layer exists:

| Layer | Reschedule |
|---|---|
| `IJourneyIngest.RescheduleAsync`, `RescheduledCall` | built |
| `/sync/push` slot `rescheduled` | built |
| Sync manager: `"RescheduledCall" → "rescheduled"` | built |
| Back office renders `rescheduledFrom` | built |
| **Anything on the device that enqueues one** | **missing** |

**This is F7 exactly, one mutation type over.** W7 slice 5 is named *"rep-side annotations —
not-visited with reason, unplanned visit, **reschedule within cycle**"*: three clauses, of which the
device had one, then two after R4, and still not three.

**Cost if left:** a rep who cannot make a call today and knows they will on Thursday has only
*not-visited* — which records a miss against coverage rather than a move.

**Fix:** a `reschedule` writer beside `addUnplanned`, and an entry point on the stop row. The journey
spec already settles the hard part (a call moves only within its cycle), so this is the same shape as
R4 and smaller — there is no picker to build.

---

### F3 — The re-price verdict is computed, stored, and read by nothing

**Where:** `FieldKit.Modules.Order/Order.cs:374` (`Agreement`), `OrderEndpoints.cs:57`
(`OrderResponse`)

W11 slice 14 taught the server to re-price a pushed order and record whether it agrees with the
device. `PriceAgreement` — `NotRepriced` / `Agrees` / `Differs` — is computed on the aggregate, and:

- it is **not** on `OrderResponse`, which carries neither `Agreement`, `ServerTotal` nor `TaxTotal`;
- the only readers in the repository are **two assertions in `OrderRepriceTests`**;
- no front-end code calls `/api/orders` at all.

**This is F1's shape from the previous sweep — a value written and never read — one layer up.** There
it was `errorCode` on the outbox; here it is the whole comparison.

**It reflects on this week's own work, and that is worth saying plainly.** W11½ **R6** existed to
stop that comparison producing *false* disagreements across a day boundary. The fix was right — a
stored verdict that is wrong is wrong whether or not anyone is looking, and `BR-ORD-2` is a promise
about the data — but the slice was justified by a rep being "reported as disagreeing", and no report
currently exists.

**Cost if left:** `BR-ORD-2`'s promise is checked and the check is invisible. A tenant whose device
and server drift would have it recorded in the database and surfaced nowhere.

**Fix:** put `Agreement`, `ServerTotal` and `TaxTotal` on `OrderResponse` — three fields on an
endpoint that already exists. **Week 12's dashboard does not cover this**: its list is coverage,
strike rate, perfect-store and order value, all aggregates. Whether a *particular* order disagreed is
an exception queue, not a KPI, and it should be an explicit W12 decision rather than an assumption.

---

### F4 — A refused order or audit still says nothing

**Where:** `frontend/components/field/todays-journey.tsx:148`, `unplanned-call.tsx:112`

W11½ R5 gave refusals a sentence. It reaches three of the five subjects a device queues under:

| Mutation | `subjectId` | Surface |
|---|---|---|
| `CapturedVisit` | visit id | round row ✅ |
| `NotVisitedCall` | planned visit id | round row ✅ |
| `UnplannedCall` | outlet id | picker row ✅ |
| **`CapturedOrder`** | **order id** | **none** |
| **`CapturedAudit`** | **audit id** | **none** |

Neither `SyncBadge` nor `RefusedReason` is mounted anywhere keyed by an order or an audit id, and
`statusOf(visitId)` does not find them — the subject is the order, not the visit it belongs to. The
connectivity indicator shows a *count* of failures; nothing names which, or why.

**Cost if left:** the case R5 was written for. An order refused on its merits — `BR-ORD-9`'s
rejection, an unknown product — leaves the rep with a number and no sentence, on the work that is
hardest to reconstruct.

**Fix:** the visit screen is the natural home for both, since a rep reaches an order and an audit
through it. Small: the components exist and take a `subjectId`.

---

### F5 — `BR-ORD-9`'s rejection loop cannot complete, and the store says so

**Where:** `frontend/lib/sync/db.ts:564`

The device's own comment is accurate and worth quoting, because it makes this a **recorded debt
rather than a discovery**:

> There is deliberately no `accepted` or `rejected` here yet: what the back office made of an order
> arrives on the *pull* feed, which does not carry orders until the Order module opts back into sync
> tracking.

Everything else is built: `POST /api/orders/{id}/rejection`, `Order.Resubmit`, the terminal-mutation
rule, ten `.http` requests. `BR-ORD-9` describes a rep correcting a flagged line and resubmitting
under a new mutation id — and **no rep can begin, because none can learn their order was rejected.**

**Cost if left:** a rejected order is stranded exactly as `BR-ORD-9` was written to prevent. It is
bounded today only because nothing rejects orders — there is no back-office screen to do it from.

**Fix:** orders on the pull feed, which the comment already names as the precondition. Larger than
the others and probably its own slice.

---

## 3. Non-findings — recorded so nobody re-opens them

Each looks like a defect and is not.

- **No back-office screens for visits or orders.** `/api/orders` and `/api/visits` are the only two
  API groups the front end never calls. That is **Week 12** — "supervisor dashboard … from module
  query contracts" — and is on the plan rather than missing from it. F3 is the part W12's list does
  *not* cover.
- **The dev tenant's outlets belong to a channel with no workflow**, while twelve *other* channels
  each have one with three steps. That is the duplicate-seed artifact this sweep's predecessor
  recorded, from repeated `.http` runs. **It is not the defect — it is what hid F1**, which is a real
  gap regardless of seed data.
- **`journey.plan.noneForDate` on every unplanned call.** Correct server behaviour, already recorded
  as F9 last sweep: an unplanned call is a row on a plan, and the dev rep has no published round.
  Now visible to the rep, which is R5 working.
- **The order is held behind the visit on the first push.** `sendable()` doing its job (W11 slice 8c):
  `CapturedOrder` stayed `pending` until check-out enqueued `CapturedVisit`, then both went.
- **No service worker under `next dev`.** Unchanged and still true: the offline shell needs
  `npm run build && npm start` to exercise.

---

## 4. The previous sweep's findings, re-checked

| | Finding | State |
|---|---|---|
| F1 | `errorCode` stored and never read | **closed** (R5) — verified live: the picker row shows the server's sentence |
| F2 | `IOrderMinimumChangeFeed` missing from the registry | **closed** (R1) — and the gate it added caught `IOutletCalendar` during R6b, unprompted |
| F3 | Order minimum mirrored with no corpus | **closed** (R7) — the file found a real divergence on exponent/hex parsing |
| F4 | Three latent test flakes | **closed** (R2) — `eventually` helper; no flake in this sweep's runs |
| F5 | Picker cannot tell two Marias apart | **closed** (R3) — verified live against eight same-named users |
| F6 | Re-pricing takes the capture instant as a UTC date | **closed** (R6a/R6b) — all four outlets now carry real zones; the order priced by the shop's day |
| F7 | No unplanned visit on the device | **closed** (R4) — used as the entry point for this sweep's whole walk |
| F8 | Second call at a worked shop routed with the planned call id | **open** — recorded during R4, still true |
| F9 | Unplanned call needs a published round | **open by design** — reproduced here; now explained to the rep rather than silent |

**Debts confirmed still open:** photo retention (`OFF-11`), `Photo`-type survey questions
unanswerable, `measured()` and `Audit.Check` sharing no evidence, form-per-channel survey
configuration, and `ORD-04`/`ORD-13`/`ORD-15` deferred by plan. The `OutletSnapshot` time-zone debt
is **closed** by R6a.

---

## 5. What this pass could not reach

- **The audit screen**, for F1's reason — no `Audit` step on this channel. The order screen was
  reached by typing its route, which is how F1 was confirmed; the audit was left alone rather than
  half-tested.
- **A planned round.** Still no published plan covering today for the dev rep, which is why every
  unplanned call is refused. The previous sweep asked for a seeded plan for *today* and it is still
  the single change that would most improve the next pass.

---

## 6. What this says about the shape of the gaps

Nine findings across two sweeps, and **six of them are the same shape**: a capability that exists
everywhere except at the point where somebody would use it.

- F7 (last sweep) — unplanned call: every layer but the device writer.
- F1 (this sweep) — order and audit capture: a working screen with no link to it.
- F2 (this sweep) — reschedule: every layer but the device writer, *again*.
- F3 (this sweep) — the re-price verdict: computed and stored, absent from its own API response.
- F4 (this sweep) — refusals for two of five subjects: the component exists and is not mounted.
- F5 (this sweep) — the rejection loop: both ends built, no path between them.

**Every test suite passes in all six.** They are not bugs in a unit; they are *absences of an edge*
between two things that each work. The previous sweep concluded that "every defect lived in a gap
between two green suites" and proposed vectors as the answer — vectors close the gap between two
**implementations of one rule**, which is why R7 found something. They cannot see a missing link on a
screen.

**What would:** a reachability check. The repository already has the two ingredients — W11½ R1's
registry gate proves that a machine-checked list of "what exists" catches drift a person will not,
and `check-vector-readers.mjs` proves the same for files. The analogue here is a gate over the
**mutation types and routes**: every `type` the sync manager can route has a producer in
`lib/`; every route under `app/[locale]/(field)` is linked from somewhere. Both are source scans of
the kind already in `scripts/`, and either would have failed on F2 and F1 the day they were written.

That is the recommendation this sweep would rather make than a tenth finding of the same kind.

---

## 7. Suggested order

1. **F1** — the largest gap in the smallest change, and the one blocking every future manual pass.
2. **F4** — mounts two existing components; the case R5 was written for.
3. **F3** — three fields on an endpoint that exists, and a W12 decision to take deliberately.
4. **F2** — `JRN-06`'s third clause, the same shape as R4 and smaller.
5. **The reachability gate** (§6) — after F1 and F2, so it lands green and stays that way.
6. **F5** — orders on the pull feed. Its own slice.
7. **F8** — still open from R4, still small.

None blocks Week 12. **F1 is the one that changes what a demo can show**, since the golden path runs
through an order.

> Two process items from the previous sweep remain worth doing regardless: **a published plan for
> today in the dev seed**, and **a production build in the loop** so the offline shell is exercised
> by something other than a person remembering to.
