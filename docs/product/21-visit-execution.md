# Functional Spec — Visit Execution

> **Module:** Visit · **Group:** Field · **Phase:** 2 · **Status:** ✅ Baseline
> **Depends on:** Journey · **Consumed by:** Audit, Order

## 1. Purpose

Visit Execution is the **spine of the in-store experience**. It models the lifecycle of a
single store call — check in, work through a **configurable set of steps**, check out — and it
is the container that Audit and Order attach to. Everything a rep does in a store belongs to a
Visit. It is designed **offline-first**: the entire flow works with zero connectivity.

## 2. Actors

| Actor | Interest |
|---|---|
| Field Rep | Executes the visit end-to-end, in-store, offline |
| Supervisor | Reviews visit outcomes, durations, coverage, compliance |
| Sales Ops / Admin | Configures the per-channel visit workflow (steps) |

## 3. Core concepts

- **Visit** — one in-store engagement: outlet, rep, planned/unplanned, timestamps, geo-stamp,
  status, outcome. Owns its child work (audits, orders, notes, photos).
- **Visit workflow** — the ordered list of **steps** for a visit, **configured per channel/
  tenant** ([A1](decisions-and-assumptions.md#a1--per-tenant-customization-config-driven-moderate)).
  Step types include: *audit*, *order*, *survey*, *task/checklist*, *photo*, *note*, *signature*.
- **Check-in / check-out** — the boundaries of a visit; check-in captures a **geo point**
  validated against the outlet **geofence**.
- **Visit outcome** — productive / non-productive (+ reason), e.g. "ordered", "no order — store
  closed".
- **Not-visited** — a planned visit the rep couldn't do (reason captured; no check-in).

## 4. Capabilities & flows

### F1 · Check in
1. Rep opens a planned (or unplanned) outlet.
2. App captures location; validates against the outlet **geofence** (radius, [OUT-08](12-outlets-master-data.md)).
   > 📝 ASSUMPTION: until OUT-08 makes the radius per-outlet, every outlet uses **150 m**
   > (`IOutletGeofence.DefaultRadiusMetres`). It is deliberately loose: a consumer GPS fix is
   > routinely tens of metres out, and a radius tight enough to be precise would flag honest reps —
   > which is the same failure mode BR-VIS-2's assumption guards against. The value lives on the
   > contract rather than in Visit, so the day it becomes per-outlet the query changes and the
   > check-in rule does not.
3. If outside the geofence → allowed with an **override reason** (flagged for the supervisor).
   The refusal that asks for it carries the **distance** and the radius, not just a verdict:
   eighty metres and two kilometres are different conversations.
4. Visit starts; timer + step list appear.

### F2 · Work the steps
- The rep progresses through the configured steps. Steps can be **mandatory** (must complete to
  check out) or optional. Audit/Order/Survey steps open the respective sub-flows.
  > 📝 ASSUMPTION: **the steps are copied onto the visit at check-in, not read from Configuration
  > as the rep works.** An admin editing the channel workflow at eleven must not change what a rep
  > who checked in at ten is required to do — they would be refused check-out for a step that did
  > not exist when they started, or released from one they had been told was compulsory. This is
  > `BR-VIS-6`'s snapshot rule applied to the one piece of reference data that decides whether a
  > visit can end, and it is also what lets the whole thing run offline (§7): the device holds the
  > visit and its steps and needs no second conversation to know what is outstanding.
  >
  > There is no *skipped* state. An optional step nobody did is left **pending**, and a mandatory
  > one cannot be skipped at all — a third state would record the same fact twice and invite the
  > question of what a skipped mandatory step means.
- **What is outstanding travels with every response**, not only with the check-out attempt. Being
  told at the door that the visit cannot end is the version of `BR-VIS-3` that sends a rep back
  into the shop.

### F3 · Check out
1. Rep completes mandatory steps; sets the **outcome**.
2. App stamps check-out time (→ time-on-site); the visit is sealed locally and queued for sync.
   > **The two ends of a visit are opposite in temperament, and that is deliberate.** `BR-VIS-2`
   > refuses to keep a rep out of a shop; `BR-VIS-3` refuses to file a visit as done while the work
   > it was configured for is not. Refusing costs nothing here — the rep is still in the shop, still
   > checked in — provided the refusal *names the steps*, which it does.
   >
   > **The check-out position is captured, never judged.** Two points are a cheap counter against a
   > visit that was never really worked; a geofence rule at this end would prompt a rep who has done
   > the job and walked to the car, which is the flag-on-ordinary-work failure `BR-VIS-2`'s
   > assumption already warns about.
   >
   > **Time-on-site is derived, not stored** — check-out minus check-in, and a stored copy is a
   > second answer that can disagree with the first.

### F4 · Not-visited
- From the journey, the rep marks an outlet not-visited with a reason (no check-in occurs).

## 5. Business rules

- **BR-VIS-1** A visit belongs to exactly one outlet and one rep; its children (audit/order)
  belong to it.
- **BR-VIS-2** Check-in captures a geo point; outside-geofence requires an **override reason**
  (visit still allowed — never block the rep, always record).
  > 📝 ASSUMPTION: **remote-capable visit types skip the override.** A phone call, a video
  > conference or a head-office meeting is legitimately not at the outlet, so demanding an override
  > reason records an exception where nothing exceptional happened — and a flag that fires on
  > ordinary work is a flag supervisors learn to ignore. The visit type should carry whether presence
  > is expected, configured per channel through `IVisitWorkflow` (Configuration module).
  >
  > **Built in W7 slice 6, one slice before check-in uses it** — the ordering is the point rather
  > than an accident: this rule cannot be written without somewhere to ask the question, so check-in
  > depends on the contract existing and not the other way round. A channel nobody has configured
  > answers **presence expected**, because the two mistakes are not equal: presence expected on a
  > remote channel records an exception for every ordinary call, which is annoying and *visible*,
  > while presence not expected on a store channel silently stops recording the one thing this rule
  > exists to capture.
  >
  > This is also the correct home for the question a per-tenant "geo validation" flag briefly tried
  > to answer in the Outlets module (#56, reverted): whether coordinates are *valid* is data
  > integrity and never optional; whether a rep must be *at* them is policy, and it belongs here.
- **BR-VIS-3** All **mandatory** steps must be complete before check-out.
  > Built in two halves, because check-out is a slice later than the steps. W7 slice 8 makes the
  > visit *answer* which mandatory steps are still open — on check-in, and on every response that
  > returns a visit — and slice 9 refuses check-out while that list is non-empty. Mandatory is read
  > from the visit's own copy of the workflow, never from Configuration, so the answer cannot change
  > under a rep mid-visit.
- **BR-VIS-4** A visit, once **checked out**, is **sealed** — device-owned, append-only, and
  **not editable after sync** (mirrors the order rule; keeps sync conflict-free — [B7](decisions-and-assumptions.md#b7--conflict-resolution-matrix)).
- **BR-VIS-5** Time-on-site = checkout − checkin; abnormally short/long visits are flagged for
  reporting, not blocked.
- **BR-VIS-6** Every visit carries the **snapshot version** of reference data it was executed
  against (for audit/repricing traceability).
  > **Half built, and the missing half is waiting for the thing that produces it.** The part that
  > matters for `BR-VIS-3` is done: a visit's *steps* are copied at check-in, so what a rep was
  > required to do cannot change under them (W7 slice 8). The general reference-data version is not,
  > because there is nothing to record yet — a version is what a device synced *against*, and Sync
  > (`W8`) is what will mint one. Storing a client-supplied string in the meantime would be a column
  > nothing writes and nothing reads, which is the sort of field that later gets trusted. It lands
  > with the sync cursor it names.

## 6. Requirements

| ID | Requirement | MoSCoW | Phase |
|---|---|---|---|
| VIS-01 | Check-in with geo capture + geofence validation | Must | 2 |
| VIS-02 | Outside-geofence override with reason | Must | 2 |
| VIS-03 | Configurable per-channel step workflow | Must | 2 |
| VIS-04 | Mandatory-step gating on check-out | Must | 2 |
| VIS-05 | Check-out with outcome + time-on-site + **check-out geo-stamp** (single point, a cheap duration-fraud counter; still two points, not a trail — consistent with the GDPR posture) + **provenance** (see below) | Must | 2 |
| VIS-06 | Notes & photos as visit steps | Should | 2 |
| VIS-07 | Not-visited handling (from Journey) | Must | 2 |
| VIS-08 | Signature capture step | Could | 3 |
| VIS-09 | Visit summary screen (recap before checkout) | Should | 3 |
| VIS-10 | Supervisor visit review (durations, overrides, outcomes) | Should | 3 |

## 7. Offline behavior

**The whole visit is offline-first.** Check-in, all steps, and check-out execute against
on-device data with no network. The completed visit (and its audit/order children, photos) is
written to the **local outbox** and pushed idempotently on reconnect; the visit is
**device-owned and append-only**, so no server-side conflict arises ([B7](decisions-and-assumptions.md#b7--conflict-resolution-matrix)).
Geo capture uses the device sensors offline; geofence data is part of the synced outlet record.

### The verdict shown is the verdict stored (W9 slice 6)

The check-in screen takes one fix when it opens, assesses it once, and writes **that** assessment to
the visit. It deliberately does not re-measure when the rep presses the button, and the reason is the
one thing this screen cannot afford to get wrong: a rep shown *inside the fence* and recorded
*outside* it has been handed an override reason they never saw, and a supervisor an exception they
would deny. The fix is requested with `maximumAge: 0` for the same reason in the other direction — a
cached position is the previous shop's car park, and the geofence would agree with it perfectly.

Three positions are all ordinary and none of them blocks (`BR-VIS-2`): inside, outside, and **no fix
at all**. The last still asks for a reason, because a phone reporting no position at a shop that has
one is both what a supervisor would want to see and how a check-in would be faked. The device's
reported accuracy is shown to the rep — forty metres outside a hundred-and-fifty metre fence means
something different when the fix is good to five metres than when it is good to eighty — but it is
**not** recorded: `CapturedVisit` is a public contract, and widening it is a decision to take on its
own rather than as a side effect of a screen.

### A step whose control does not exist yet (W9 slice 7)

The device renders the sequence from **the visit**, never from Configuration — the copy check-in took
under the snapshot rule above. Of the seven step types, W9 builds controls for two: `Task` is
complete as a checklist tick, and `Note` captures its text (`VIS-06`). `Audit`, `Order`, `Survey`,
`Photo` and `Signature` open sub-flows that arrive in W10, W11 and Phase 3.

Until then those steps render as what they already are — a labelled item on a checklist the rep works
in the shop — and can be marked done. **The alternative is a mandatory step nobody can complete**,
which by `BR-VIS-3` is a rep who cannot check out: the visit would be broken by a feature not being
finished yet.

What that costs is worth stating rather than discovering: a ticked `Audit` step records that the rep
did an audit and carries none of its numbers. `CapturedStep` sends the type alongside the label, so
the back office can see exactly which kind of step was ticked rather than inferring it from a name an
admin chose.

A step type the *device* does not recognise is named generically and stays completable, for the same
reason: a device is offline-first and therefore routinely older than the server, and a tenant
configuring a newer step type must not leave a rep with a blank mandatory row and no way out.

### Leaving, and the two positions that are not the same kind of fact (W9 slice 8)

Check-out is enforced on the device because there is nobody else to enforce it: `BR-VIS-3` refuses to
seal a visit while a mandatory step is open, and a rep with no signal has to be told *now*.

**What is outstanding is on screen for the whole visit**, listed by name, rather than appearing when
the rep tries to leave. §F2 already says the outstanding set travels with every response; on the
device that becomes a permanent list, because being told at the door is the version of this rule that
sends someone back into a shop they have walked out of. The check-out control stays live anyway — a
rep who taps it is told which steps, which is more use than a disabled button with no explanation.

**The check-out position is taken at the tap, and this is deliberately the opposite of check-in.**
Check-in takes one fix when the screen opens and honours the verdict it displayed; nothing about
check-out's position is shown before it is stored, so the most truthful moment is the last one. It
waits five seconds and then records `null` — `BR-VIS-3` is the only thing allowed to keep a rep in a
shop, and a satellite is not. Nothing judges it, per §F3.

**Time on site is derived on the device exactly as it is server-side** — check-out minus check-in,
never stored (`BR-VIS-5`).

### The recap, and why it is not an interstitial (`VIS-09`, W9 slice 10)

"Recap before check-out" reads naturally as a confirm screen between the button and the seal. It is
not one. That shape taxes every visit of every day with an extra tap in order to catch a mistake on a
few of them, and what a rep needs is the *information*, not the ceremony — so it sits inline, above
the outcome, where they are already looking when they decide.

It deliberately does not restate the step list above it. Three of its four facts are unavailable
anywhere else on the screen:

- **Optional steps still open.** `BR-VIS-3` gates check-out on *mandatory* steps, so the check-out
  panel names those and stops. An optional step nobody did is the one thing a rep can still act on
  and nothing else tells them about. Mandatory ones are deliberately absent here — one fact in two
  places makes the lists look like they disagree.
- **Time in the shop while the visit is open**, which is otherwise visible only after sealing.
  Computed at render rather than ticking: a clock counting up in a rep's face is pressure, and the
  number is a fact `BR-VIS-5` derives afterwards, not a target.
- **That check-out is final.** A visit seals and queues; nothing edits it afterwards, on the device or
  in the back office. That is worth one sentence at the moment it becomes true.

### Provenance — how the record says where it came from

An offline visit arrives carrying **the device's** timestamps, position and geofence verdict, and the
server stores all of them unmodified: re-judging yesterday's visit against today's radius would
reclassify a rep who was legitimately inside it. That is right, and it leaves two facts about the
visit that only the *server* can know, so the visit records both (W9 slice 0):

| Field | Meaning |
|---|---|
| `source` | `Live` (worked through `/api/visits/check-in`) or `Device` (drained through `/sync/push`). `null` for visits stored before this was tracked — there is nothing to backfill it from, and a default would make those rows claim something nobody recorded |
| `recordedAtUtc` | When this server first stored the visit. For a live visit that is check-in; for an ingested one, the distance from `checkedOutAtUtc` is how long the work sat on a phone |

**`source` is stored rather than computed from the gap**, because a rep who checks out in a shop with
signal drains within seconds — that visit's timestamps are indistinguishable from a live one's, and
only the discriminator tells them apart.

**Neither is a rule.** A device claiming a check-out later than the moment the server received it is
claiming the future, and nothing acts on that: what a *legitimate* drain lag looks like is a question
for `VIS-10`/W13 reporting against a real population, and a rep with no signal until Friday is
precisely the case this design exists for. `BR-VIS-5` already says the same about visit duration —
these are reporting facts, and they never block.

## 8. Module contract (exposed to others)

- `IVisitContext` — the current/opened visit a step attaches to (used by Audit, Order).
- `IVisitQuery` — how a set of visits **came out**, for reporting (`VIS-10`).
  > Built in W12 slice 1, and narrower than this line promised. It answers **counts by outcome** over
  > a set of outlets and a date window — productive, non-productive, and still-open kept separate —
  > rather than "visits for an outlet/rep/day". Counts because both KPIs that want it are ratios
  > (strike rate, coverage), and handing back rows would move the arithmetic, and with it the
  > judgement about what *productive* means, into whoever asked. It takes **outlet ids** rather than a
  > territory: a visit knows its shop and its rep and nothing about org structure, so the caller
  > resolves scope first. Reading a **single** visit back is deliberately still absent — its caller is
  > the supervisor review screen, and it lands with it.
- `IVisitIngest` — apply a pushed visit through this module, used by **Sync** ([module boundaries §7](../architecture/10-module-boundaries.md#7-module-registry)).
- Consumes `IJourneyQuery`, `IOutletGeofence` (Outlets — where the shop is and how close counts as
  there; separate from `IOutletCatalog` so a rep's device syncs coordinates without the commercial
  record), `IOutletClassification` (which channel the outlet is in), and `IVisitWorkflow`
  (Configuration — presence policy and the config-driven step sequence, VIS-03).
- Publishes `VisitCompleted` (with children summary) → reporting/Sync. An **amended** child order
  (BR-ORD-9) re-emits a `VisitCompleted`-correction so reporting/strike-rate stay accurate.
  > Built in W7 slice 9. The "children summary" is the two **step counts** today — how many the
  > workflow asked for and how many were done; mandatory ones are necessarily all of them
  > (`BR-VIS-3`), so a gap is optional work the rep chose to skip, which is the reporting question
  > the pair exists to answer. Audit and order counts join it when those modules exist. It carries a
  > summary and not the visit: notes, positions and override reasons stay in the module that owns
  > them, and what travels is enough for a consumer to decide whether the visit interests it. The
  > correction event is `BR-ORD-9`'s and lands with Order.

## 9. Acceptance criteria (sample)

- A rep with no signal can check in (with geofence validation), complete mandatory audit+order
  steps, and check out; on reconnect the full visit lands server-side exactly once.
- Attempting check-out with an incomplete mandatory step is blocked with a clear prompt.

## 10. Open questions

- Can a rep run **two visits** to the same outlet in a day (e.g. redelivery)? (Assumed: yes,
  each a distinct visit.)
- Is a supervisor allowed to reopen/annotate a sealed visit server-side? (Assumed: annotate
  only, never edit rep data.)
