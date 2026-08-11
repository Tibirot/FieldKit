# Phase 2 demo — a field round, offline

> **Status:** ✅ Script · **Shows:** `JRN-05`, `VIS-01`…`VIS-07`, `VIS-09`, `OFF-01`…`OFF-06`
> **Delivery plan:** [W9](../delivery-plan.md#week-9--field-pwa--offline-journeyvisit-) · **Last updated:** 2026-08

The recording this script produces is the one the README leads with: **sync a round → lose signal →
work a day → reconnect → the back office has it.** Everything below is reproducible from a clean
checkout, which is the point of writing it down rather than improvising on the day.

> **What this document is not.** It does not record anything. Recording is a person with a screen
> capture and thirty seconds of patience; what a repo can own is *what to show, in what order, and
> what each step is evidence of* — so that the demo is the same demo next month, and so a step that
> stops working is noticed rather than quietly dropped from the take.

## 0 · Before you press record

```bash
dotnet run --project FieldKit.AppHost
```

Aspire assigns the ports; read the front end's from the dashboard rather than assuming `:3000`.

Seed a tenant the way an administrator would — every request is in
[`FieldKit.Server.http`](../../FieldKit.Server/FieldKit.Server.http), in order: a role and a user, an
org unit and a position, a channel and five outlets, a territory holding four of them with the rep
assigned, a catalogue with prices and a promotion, and a visit workflow for the channel.

Then, for the rep's subject: a **call frequency**, a **working calendar**, and a plan **generated and
published** over a window that includes today.

> **Leave one shop out of the territory.** `RO-…-0005` exists in the tenant and is not the rep's, and
> it is what makes step 2 mean something: the round that reaches the phone is *scoped*, not *all the
> outlets*. A demo where every shop syncs proves nothing about scoping.

**Set the viewport to 375 × 812** before recording. The field app is a phone app; showing it at
desktop width undersells every layout decision in it.

## 1 · The round, from the back office

Show the published plan: the calls, the shortfalls the generator recorded, and the fact that
publishing is one-way (`journey.plan.alreadyPublished` on a second attempt).

> **Evidence:** `JRN-03`, `JRN-04`. Generation is a pure function of frequency, calendar, territory
> and window — say so, because it is the reason the plan is reproducible rather than a snapshot.

## 2 · The phone picks it up

Open `/field` as the rep. The device binds on first run, pulls, and today's journey renders.

Show **This device**: shops held, waiting to send, last synced, storage used.

> **Evidence:** `OFF-03`, `OFF-05`, `OFF-12`. Four shops, not five — the fifth is in the tenant and
> not in the rep's territory. The number on screen is the scoping rule, visible.

## 3 · Go offline — and stay offline for the whole of step 4

Turn the network off at the OS or in dev tools. **Do not turn it back on until step 5.** Every screen
from here reads the local store; nothing below waits on a request.

## 4 · A day's work, with no signal

| Do | Evidence |
|---|---|
| Open a stop, watch the geofence assess **on the device** | `VIS-01`, `VIS-02` |
| Check in *outside* the fence, type a reason | `BR-VIS-2` — never blocks, always records |
| Work the steps; save a note | `VIS-03`, `VIS-06` |
| Try to check out with a mandatory step open | `BR-VIS-3` refuses **by name** |
| Read the recap, then check out | `VIS-09`, `VIS-05` |
| On another stop: report it **not visited** with a reason | `VIS-07`, `JRN-06` |

The round now shows *Worked*, *Not visited* and *To do*, each with a **Not synced** badge, and the
chrome counts the items waiting.

> **Evidence:** `OFF-01`, `OFF-02`. Two things are worth pausing on. The geofence verdict is decided
> here, offline, and the server stores it unmodified — there is no second opinion. And the badges
> answer a *different* question from the status chips beside them: one is "did the rep do it", the
> other "does the back office know".

## 5 · Reconnect

Turn the network back on. The outbox drains: the badges clear, the chrome reads *Everything synced*.

> **Evidence:** `OFF-04`, `OFF-06`. Nothing was retyped and nothing was lost. The drain is idempotent
> — pressing **Sync now** again is free, which is the property the whole protocol is built on.

## 6 · The back office agrees

`GET /api/visits/{id}` for the visit worked offline, and the plan for the annotated call.

Point at four fields:

- **`source: "Device"`** — this visit was drained off a phone, not worked in the back office.
- **`recordedAtUtc` later than `checkedOutAtUtc`** — the drain lag, visible. A visit that sat on a
  phone for three days is distinguishable from one that happened three days ago.
- **`checkInDistanceMetres` and `wasInsideGeofence`** — the *device's* measurement, unmodified.
- **`timeOnSiteSeconds`** — derived here and on the phone, stored in neither.

And on the plan: the call reads `NotVisited` with the rep's own sentence.

> **Evidence:** `VIS-05`, and W9 slice 0's whole reason for existing. This is the frame that says the
> round trip is real rather than a screen recording of hopeful UI.

## What to say while it runs

Three sentences, and they are the ones the rest of the repo is arguing for:

1. **The device decides.** Geofence, mandatory steps, the reason a non-productive visit owes — all
   re-implemented on the phone, because offline there is nobody else to run them.
2. **The server never re-judges.** It stores what the device decided, so a radius changed next month
   cannot reclassify a rep who was legitimately inside today's.
3. **Nothing is lost and nothing is duplicated.** Work is durable before the UI confirms it, and
   every mutation carries an id the server's ledger answers for — so a retry is free.

## If a step does not work

That is the script earning its keep: a demo that quietly drops a step is a demo that stops proving
what it claims. Open an issue rather than re-taking without it.
