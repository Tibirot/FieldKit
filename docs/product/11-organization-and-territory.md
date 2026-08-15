# Functional Spec — Organization & Territory

> **Module:** Organization · **Group:** Admin · **Phase:** 1 · **Status:** ✅ Baseline
> **Depends on:** IAM · **Consumed by:** Outlets, Journey, Visit

## 1. Purpose

Organization models **who sells where**. It captures the sales hierarchy (the people and their
management lines) and the **territories** that carve the market up, then **assigns reps to
territories**. Journey planning and outlet ownership both hang off this: an outlet belongs to a
territory, a territory is served by a rep, a rep reports up a management chain.

## 2. Actors

| Actor | Interest |
|---|---|
| Sales Ops / Admin | Build the org tree, define territories, assign reps |
| Supervisor | Owns a branch of the tree; sees their team's territories |
| Field Rep | Is assigned to one (or more) territories that scope their world |

## 3. Core concepts

- **Org unit** — a node in the sales hierarchy (e.g. Country → Region → Area → Team). Tenants
  configure the depth/labels.
- **Position / assignment** — a user occupying a role in the tree (e.g. Andrei = Supervisor of
  *Area North*), establishing the management line.
- **Territory** — a bounded slice of the market a rep is responsible for. Defined by the set of
  **outlets** it contains (membership-based; optionally aided by geo/postal rules).
- **Rep–territory assignment** — which rep serves which territory, over a validity period.

## 4. Capabilities & flows

### F1 · Model the org hierarchy
1. Admin builds the tree of org units and assigns users (supervisors/reps) to positions.
2. The management line is derived (used for roll-up reporting and visibility scoping).

Two shapes of that derivation, from the same tree and answering different questions:

- **Management line** — the units *above* someone, nearest first. Who they report up through.
- **Visibility scope** — their units and everything *below*. What BR-ORG-4 describes.

A **position's title is a label, never a capability.** It is free text an admin types: it can read
"Supervisor" for someone holding no supervisory permission, and anyone with `position:write` can
change it. What a user may do comes from their token and nowhere else (BR-IAM-2) — the moment
something branches on the title, the permission model has a second, editable, unenforced copy.

Positions are **current state, not history**. A row means "is attached now". `ORG-08` (reassignment
with history) is Phase 2, and BR-ORG-5 does not depend on this either way: a visit or an order
records the user who made it, so its attribution survives any change to the org chart.

### F2 · Define territories
1. Admin creates a territory and assigns outlets to it (directly, or by geo/postal/segment
   rule that materializes membership).
2. An outlet belongs to **exactly one** primary territory at a time.

A territory **belongs to an org unit**, and that is required rather than optional: BR-ORG-4 says a
supervisor sees the territories under their branch, so a territory under no branch would be visible
to nobody by that rule — and making it optional would mean inventing a second visibility rule for the
territories that had skipped the first.

**Single-primary (BR-ORG-1, `ORG-05`) is a unique index on the outlet**, not a check in code. One row
per outlet makes it a fact about the table rather than a rule every write path has to remember —
including the bulk ones that do not exist yet.

**Reassigning an outlet is refused, not performed silently.** A territory's membership *is* a rep's
offline data scope (BR-ORG-3), so moving an outlet changes what somebody's device downloads tomorrow
morning. It has to be removed from its current territory first — the same two-step this module
requires for moving a position, and for the same reason: the audit trail should show both halves.

The membership lives in Organization rather than on the outlet, because Organization owns territories
and Outlets must not depend on it. It is keyed by outlet id across a schema boundary, so there is no
foreign key; outlets are validated and labelled through the Outlets module's `IOutletCatalog`
contract, never by reading its tables.

### F3 · Assign reps
1. Admin assigns a rep to a territory (with an effective date range).
2. This assignment is the input to **journey generation** and defines the rep's **offline data
   scope** ([A4 territory-scoped](decisions-and-assumptions.md#a4--offline-data-scope-territory-scoped)).

#### Effective periods (`ORG-04`)

An assignment runs **from a date, optionally to a date**, inclusive at both ends — that is how people
write and read them, and "1–31 March" covers 31 March. An open end means "until further notice".

**Overlaps are rejected (BR-ORG-2), and touching counts as overlapping.** Two assignments sharing a
single day would mean two reps covering one territory that day. Adjacent ones — one ending the 20th,
the next starting the 21st — are the handover case and are allowed.

**"Is this current" is resolved in the *calling user's* timezone**, not the record's. A territory
spans outlets that may sit in different zones, so there is no such thing as "territory time"; the
zone that makes a back-office screen agree with the person reading it is theirs. Until account
provisioning (`IAM-10`) links Keycloak accounts to FieldKit profiles, callers without a profile fall
back to UTC — which today is the common path, not the edge case.

Assignments are **editable in place**, subject to the overlap rule: correcting a mistyped start date
should not need a cancellation and a replacement. Reassignment with retained history as a
first-class concern is `ORG-08`, Phase 2.

Every change publishes **`RepAssignmentChanged`** through the outbox, naming the incoming *and*
outgoing rep. A territory's membership is a rep's offline data scope (BR-ORG-3), so this is the
moment a device's contents should change — in both directions, which a consumer could not work out
from the incoming rep alone. It deliberately does not carry the territory's outlets: that list is
stale the moment membership changes, which happens independently of assignments.

### F4 · Reassignment / coverage
- Reassign a territory to a different rep (e.g. holiday cover) with an effective date; history
  is retained.

## 5. Business rules

- **BR-ORG-1** An outlet has exactly one **primary** territory at any moment (secondary/overlay
  territories are a *Could*, Phase 4).
- **BR-ORG-2** A territory has at most one **active** rep assignment at a time; overlapping
  assignments are rejected.
- **BR-ORG-3** A rep's offline scope = the union of outlets in their active territory
  assignment(s) ([A4](decisions-and-assumptions.md#a4--offline-data-scope-territory-scoped)).
- **BR-ORG-4** Visibility: a supervisor sees data for territories under their org branch;
  enforced via permissions + the management line.
- **BR-ORG-5** Reassignment never deletes historical visits/orders — those stay attributed to
  the rep who made them.

## 6. Requirements

| ID | Requirement | MoSCoW | Phase |
|---|---|---|---|
| ORG-01 | CRUD org units; build a configurable-depth hierarchy | Must | 1 |
| ORG-02 | Assign users to positions; derive the management line | Must | 1 |
| ORG-03 | CRUD territories; assign outlets to a territory | Must | 1 |
| ORG-04 | Assign a rep to a territory with an effective date range | Must | 1 |
| ORG-05 | Enforce single-primary-territory per outlet | Must | 1 |
| ORG-06 | Rep offline scope resolvable from active assignments | Must | 2 |
| ORG-07 | Territory membership by geo/postal/segment rule | Should | 2 |
| ORG-08 | Reassignment/cover with history retained | Should | 2 |
| ORG-09 | Supervisor visibility scoping via management line | Should | 2 |
| ORG-10 | Secondary/overlay territories | Could | 4 |

## 7. Offline behavior

Read-only on device: the rep's app knows its territory + assignment (to scope sync) but org
administration is a back-office, online activity. A reassignment takes effect on the rep's next
sync (they receive a new outlet/journey scope).

## 8. Module contract (exposed to others)

- `ITerritoryDirectory` — territory of an outlet; outlets of a territory; active rep of a
  territory.
  > **The second of those arrived in W12 slice 3** as `OutletsInAsync(territoryId)`, and it arrived
  > because nothing could answer it. Every reporting aggregate takes outlet ids, and when
  > `/api/reporting/summary` came to produce that list there was no contract that could:
  > `IOutletCatalog` resolves ids it is given, `ForOutletsAsync` maps the other way, and `IRepScope`
  > answers about one rep on one day.
  >
  > Null means **every territory**, not every outlet — so a shop nobody has been made responsible for
  > is outside every scope, including the unfiltered one. That is the honest reading of a coverage
  > report (an unassigned shop has no round to be measured against) and it also keeps the
  > per-territory figures adding up to the unfiltered one. An unknown or foreign territory id answers
  > **empty** rather than erroring, so the endpoint cannot be used to probe for somebody else's.
- `IRepScope` — resolve a rep's current offline scope (outlet ids) for Sync.
- `IOrgHierarchy` — management line / visibility scope for a user (used by reporting & auth).
- Publishes `RepAssignmentChanged` (integration event) → Sync/Journey react.

## 9. Acceptance criteria (sample)

- Assigning a second active rep to a territory is rejected with a clear error.
- Moving an outlet to a new territory changes the offline scope of both affected reps on their
  next sync.

## 10. Open questions

- Do reps ever legitimately serve overlapping territories concurrently? (Assumed: no in v1.)
- Is territory membership primarily manual or rule-driven for the demo data? (Assumed: manual +
  optional rule.)
