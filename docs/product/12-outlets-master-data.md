# Functional Spec — Outlets (Master Data)

> **Module:** Outlets · **Group:** Admin · **Phase:** 1 · **Status:** ✅ Baseline
> **Depends on:** Organization · **Consumed by:** Journey, Visit, Audit, Order

## 1. Purpose

Outlets is the **trade universe** — the retail points of sale a rep visits. It is the master
data that anchors journeys (where to go), visits (where you are), audits (whose shelf), and
orders (who's buying). Getting this clean and well-classified is what makes everything
downstream possible.

## 2. Actors

| Actor | Interest |
|---|---|
| Sales Ops / Admin | Maintain accurate outlets, classification, and geo |
| Field Rep | Sees their outlets; can propose corrections from the field |
| Supervisor | Reviews coverage of the outlet base |

## 3. Core concepts

- **Outlet (POS)** — a retail location: name, code, address, **geo-coordinates**, **IANA
  timezone**, status.
- **Timezone** — an explicit IANA zone on the outlet (e.g. `Europe/Bucharest`). Required because
  promotion validity ([BR-PRD-6](13-products-and-pricing.md#5-business-rules)) and a visit's
  business "day" resolve **in the outlet's timezone**, and a rep may cross zones. Seeded from geo on
  import, editable; not derived on-device.
- **Channel** — trade classification (e.g. Modern Trade, Traditional Trade, HoReCa). Drives
  assortment, pricing, visit workflow, and audit forms.
- **Segment / tier** — a finer grade (e.g. A/B/C by volume) influencing call frequency.
- **Banner / chain** — the retail group an outlet belongs to (optional).
- **Order-block / credit standing** — a flag that **blocks order submission** (e.g. a debtor on
  credit hold). Checked at order submit and on the sync push path (as-of-now); a blocked outlet
  rejects the order with reason `OUTLET_ON_HOLD`.
- **Contacts** — people at the outlet (store manager, buyer); **personal data**
  ([B8](decisions-and-assumptions.md#b8--privacy--gdpr-posture)).
- **Custom fields** — per-tenant attributes ([A1 config-driven](decisions-and-assumptions.md#a1--per-tenant-customization-config-driven-moderate)).
- **Geofence** — the outlet's location + radius, used by Visit check-in.

## 4. Capabilities & flows

### F1 · Maintain outlets
- CRUD outlets with classification (channel, segment, banner), address, geo, contacts, and
  tenant custom fields. Bulk import for onboarding.

#### Location, time zone and contacts

**The address is structured**, not one free-text block: postal code and city are what territory
membership rules key off (`ORG-07`), and a single string would make those rules parse prose. Every
part is optional — onboarding data is routinely half-known, and an address that must be complete
before it can be recorded means a half-known outlet cannot be recorded at all.

**The country is the one part that is normalised, because another module decides with it.** It is
stored upper-cased and refused unless it is two letters (`outlet.countryCode.invalid`), which is the
only shape [ISO 3166-1 alpha-2](https://www.iso.org/iso-3166-country-codes.html) has. Optional still
means optional: the rule applies to a country that was given, not to one that was left out.

Both halves fix a silent failure rather than a loud one. `TaxRate.Create` upper-cases its country and
tax resolution compares the two directly ([PRD-07](13-products-and-pricing.md)), so an outlet stored
as `"ro"` matched no rate — and "no rate" is indistinguishable from a tax class nobody has priced,
which is the distinction `PRD-07` exists to keep. Nothing errored and nothing logged; the outlet was
simply untaxed. Refusing `"Romania"` rather than truncating it to `"Ro"` is the same failure avoided
from the other end: the truncation fits the column and is wrong forever after.

**The time zone is required and explicit** — `Europe/Bucharest`, never an offset, and never derived
from the coordinates. A visit's business "day" and a promotion's validity ([BR-PRD-6](13-products-and-pricing.md#5-business-rules))
both resolve in it, a rep may cross zones during a shift, and an offset is wrong twice a year.
Deriving it on the device would make the answer depend on which device asked. It is validated against
the runtime's zone database rather than a pattern, because `Europe/Bucuresti` is well-formed and does
not exist.

**Coordinates are optional, and when supplied they are always validated.** Two rules that are easy to
conflate and are not the same thing:

- Whether an outlet *has* coordinates is optional — onboarding data routinely arrives without them,
  and BR-OUT-2's "required for outlets that participate in journeys" lands with the Journey module,
  where participation is actually defined.
- Whether a supplied pair is a real point on the earth is **not** a tenant policy. Latitude 91 is
  meaningless for every kind of outlet and every kind of visit, and storing it produces a pin in the
  ocean that nothing later can distinguish from a real one.

What *is* a policy — and lives in Visit, not here — is whether a rep must be **standing at** the
outlet. [BR-VIS-2](21-visit-execution.md#5-business-rules) already answers it: an out-of-geofence
check-in is allowed with a recorded override reason, and the rep is never blocked. Remote-capable
visit types (a phone call, a video conference, a head-office meeting) refine that further — see the
Visit spec.

> This replaced a per-tenant `validateGeoCoordinates` flag, briefly shipped in #56. It gated the one
> thing that is not a policy, while the thing it was meant to enable was already specified two
> modules away. Recorded because the reasoning is more useful than the code was.

**Contacts are personal data** ([B8](decisions-and-assumptions.md#b8--privacy--gdpr-posture)). They
are replaced wholesale on update rather than patched — a delta needs the caller to know the current
state, and two people editing one outlet would interleave silently. It also gives erasure a trivial
shape: an empty list removes every contact, and the rows are deleted rather than flagged. A dedicated
erasure workflow is `OUT-10`.

> **Wholesale means a client has to send the list back on every save.** Omitting `contacts` from a
> `PUT` is not "leave them alone" — it is a full replacement with nothing, which is correct for a
> `PUT` and is exactly how the back-office form deleted every contact on every outlet it saved. The
> form rendered no contacts and sent none, so fixing a typo in a name erased the people recorded at
> that shop, with nothing on screen to say so. The note stays after the fix, because the trap is a
> property of the design and the next client will meet it too.

**Only the name is required**, and the sizes are checked in front of the write rather than at the
column:

| Field | Rule |
|---|---|
| `name` | Required; at most 200 characters |
| `role` | At most 100 |
| `phone` | At most 50 |
| `email` | At most 320 (RFC 5321), and something either side of exactly one `@` |

The name, because it is what a rep says at the counter. The rest is how to reach the person and any
of it may simply not be known yet. **The email check is deliberately shallow** — it catches a phone
number pasted into the wrong box, which is the mistake that actually happens, while deliverability is
only ever settled by sending mail and a stricter pattern rejects addresses that work.

The lengths matter more than they look. Before this the column widths were the only check, so a name
one character too long reached the database and came back as a `500` — the API reporting a caller's
correct-looking payload as a server fault. Every problem names the contact it is about
(`contacts[1].email`), because a form showing three people cannot work out which one "not an email
address" refers to.

### F2 · Classify & assign
- Assign an outlet to a **channel** (mandatory — it drives assortment/pricing/workflow) and to
  a **territory** (via Organization).

### F3 · Field-originated changes
- A rep can **propose** an outlet correction (moved location, new contact, wrong data) from the
  field; it enters a review queue rather than editing master data directly.
- A rep can **request a new outlet** (prospecting) → review → becomes real master data.

### F4 · Lifecycle
- Outlets can be `Active`, `Inactive` (temporarily not visited), or `Closed` (permanent).

`Closed` is **terminal**: an outlet cannot be reopened. That is what makes it mean anything beyond
`Inactive` — a status that can be walked back is just a long-lived `Inactive`, and BR-OUT-4's
"excluded from new journeys, retains history" would be a preference rather than a fact. A location
that genuinely reopens is a **new outlet with its own code**, because its trading history as a
different business should not silently continue.

> 📝 ASSUMPTION: no back-office "reopen" escape hatch. An outlet closed by mistake has to be
> re-created under a new code, which loses the link to its history. If operators hit this in
> practice, the answer is an explicit, separately-permissioned reopen that records who did it —
> **not** relaxing the transition, which would take the meaning out of `Closed`.

Status changes go through their own endpoint rather than the edit form. "This store is shut" is a
different decision from "the name was spelled wrong", and merging them lets a careless update close
an outlet as a side effect of fixing a typo.

**Every transition is recorded, append-only.** Neither `Inactive` nor `Closed` deletes anything — but
the outlet's own audit stamps are overwritten by the next ordinary edit, so without a trail an outlet
closed in March and renamed in April reads as though nobody ever closed it. The trail holds
`from → to`, the reason, when, and who; it starts with the outlet's creation (`from` is null), so
"no history" can never be mistaken for "the history was lost". There is no API to write, edit or
delete an entry — an audit log with a write path is one that can be arranged after the fact.

**"Who" is two facts, not one.** The trail is keyed on the Keycloak subject, and the response carries
that subject beside the display name resolved from it (`changedBy` and `changedByName`). Both,
because a display name is a mutable label on an account while the subject is the identity: storing
the name would let a rename rewrite who did what in March, and showing only the name would make two
colleagues who share one indistinguishable. The name is joined **at read time** through IAM's
`IUserDirectory` rather than by reading its tables ([ADR-0005](../architecture/adr/0005-postgres-schema-per-module.md)),
which also means a profile created after the fact still explains work already done.

It is resolved **server-side, not by the caller**, and that is the deciding constraint rather than a
convenience: reading this trail needs only `outlet:read`, while the user list needs `user:read`. A
front end doing the join would show raw subject GUIDs to exactly the readers least able to resolve
them. `changedByName` is null whenever the subject matches no user in this tenant — a deleted
account, an import service principal, a subject predating the user record — and the entry still
renders, falling back to the subject. Dropping such rows would be an audit trail editing itself.

The back office reaches this through a **lifecycle panel below the outlet edit form and outside it**
(W5). Outside, because a control inside the form would undo the separation the endpoint exists to
create — one Save covering both "this store is shut" and "the name was spelled wrong". A closed
outlet gets no control at all rather than a select whose every option the API would refuse; it gets
the paragraph above instead. The trail renders under both, readable by anyone with `outlet:read`:
reading why a shop was closed is not the same authority as closing one, and "why can't I order for
this outlet" is answered there or nowhere.

**A reason is required to close, and optional otherwise.** Closing is irreversible and removes the
outlet from every future journey, so *why* is the question an auditor will ask about it, and the
person who knows the answer is the one doing it. Demanding a reason for a routine
`Active`↔`Inactive` toggle would buy a column full of ".".

## 5. Business rules

- **BR-OUT-1** Every outlet has a **channel** and a **primary territory**. The channel is required on
  write; the territory is *shown* rather than required, because outlets are created before anyone
  decides who covers them — the rule describes a configured tenant, not a precondition for storing a
  shop. Organization owns the answer and Outlets asks for it through `ITerritoryDirectory`
  ([module boundaries](../architecture/10-module-boundaries.md#two-modules-may-point-at-each-other)),
  one call per page rather than one per row.
- **BR-OUT-2** Geo-coordinates are required for outlets that participate in journeys/geofenced
  check-in (validated on save).
- **BR-OUT-3** Field-originated changes are **proposals**; master data is only mutated by an
  authorized back-office approval (keeps the reference data server-authoritative — [B7](decisions-and-assumptions.md#b7--conflict-resolution-matrix)).
- **BR-OUT-4** A `Closed` outlet is excluded from new journeys but retains history.
- **BR-OUT-5** Custom fields validate against the tenant's field definitions.

## 6. Requirements

| ID | Requirement | MoSCoW | Phase |
|---|---|---|---|
| OUT-01 | CRUD outlets with channel, segment, geo, address, contacts | Must | 1 |
| OUT-02 | Per-tenant custom fields on outlets | Must | 1 |
| OUT-03 | Assign outlet to channel + territory | Must | 1 |
| OUT-04 | Outlet lifecycle (Active/Inactive/Closed) | Must | 1 |
| OUT-05 | Bulk import of outlets (onboarding / demo seed) | Should | 1 |
| OUT-06 | Rep-proposed outlet corrections → review queue | Should | 3 |
| OUT-07 | Rep-requested new outlet (prospecting) → review | Should | 3 |
| OUT-08 | Geofence config (radius) per outlet/channel | Should | 2 |
| OUT-09 | Map view of the outlet base | Could | 4 |
| OUT-10 | Contact PII handling & erasure hooks | Could | 4 |

### Bulk import (`OUT-05`)

`POST /api/outlets/import` takes the file as the request body, with **`Content-Type` choosing the
reader**. CSV is read today; JSON and Excel are follow-ups that add a reader and nothing else (see
[§6.1](#61-import-formats-still-to-come)). Held to `outlet:write` — four thousand outlets is more
volume than one, not a different capability.

**Import is not a back door.** Every rule enforced by `POST /api/outlets` is enforced here, through
the same domain factory, the same custom-field validator (`BR-OUT-5`), the same coordinate bounds and
the same time-zone check. An importer with a laxer path becomes the way bad data enters, and every
feature downstream inherits rows the API could never have produced.

The one thing it does that the API does not is **coerce**, because a CSV has no types: `chiller_count`
arrives as the text `"3"` and the validator would rightly refuse it as "must be a number". Coercion
reads the tenant's own definitions (`CFG-01`) and converts text to the declared type — and then the
identical validator runs. Coercion is parsing; the rules are unchanged. It is also only possible
because Configuration exists to be asked: without the catalogue there is nothing to coerce *towards*.

**The admin chooses what happens when rows are bad**, because both answers are right for different
files:

| Mode | Behavior |
|---|---|
| `AllOrNothing` (default) | One bad row and nothing is written. |
| `Partial` | The good rows are written; the bad ones come back to be fixed and re-sent. |

Both are **atomic** — every row is validated before anything is written, and the write is one
transaction. The mode chooses *which set* is written, never whether the write can half-apply, which
is what makes a retry safe either way. `dryRun=true` runs everything and writes nothing.

Partial mode returns **`rejectedRowsCsv`**: the refused rows in the shape they arrived, plus an
`import_error` column. Without it, an admin who imports 3,988 of 4,000 rows must hand-build a 12-row
file, because re-sending the original would now collide with everything that landed. Returned inline
rather than behind a link — a synchronous import has no result to outlive its response.

Decisions worth stating, because each has a plausible opposite:

- **Insert-only.** An existing `code` is reported, never overwritten. An import that updates would let
  a stale spreadsheet silently revert back-office corrections across the whole base — and
  `BR-OUT-3`'s point is that master data changes by a deliberate authorized act.
- **Channels are resolved by name and never created.** A typo in one cell would otherwise mint
  "Modren Trade" as a permanent classification that assortment and pricing rules key off. That is why
  `channel:write` is a separate permission, and this path does not hold it.
- **Reading ignores case; writing keeps it.** A file saying `modern trade` means the tenant's
  `Modern Trade`, and `OUT-1` and `out-1` are one shop written two ways. Stored values keep whatever
  capitalisation they arrived with — only comparison ignores it. This is a **database guarantee**, not
  an importer convention: channel names and outlet codes are unique per tenant over `lower(…)`. Before
  that index the claim in `Channel`'s own source — "two channels with one name are a data-entry
  accident" — was stated but unenforced, and an assortment rule keyed to `HoReCa` would silently miss
  every outlet filed under `Horeca`.
- **Duplicate codes are caught within the file**, not left to the unique index — an exception mid-save
  is not the row number the admin needs.
- **Unused columns are named, not dropped in silence.** A real export is full of `legacy_id`, so
  refusing the file would be hostile; but a mistyped custom-field header looks identical, and passing
  it over without a word is how a column of data goes missing quietly.
- **Contacts are not importable.** A flat row cannot hold a list, and contacts are personal data
  ([B8](decisions-and-assumptions.md#b8--privacy--gdpr-posture)) — a bulk path for PII deserves its
  own decision rather than arriving as a side effect of an outlet import.
- **Territory is not assigned on import.** `BR-OUT-1` names a primary territory, but membership is an
  Organization-side act and the create API does not require one either. Enforcing it only on the
  import path would make import stricter than the endpoint it mirrors, and having Outlets write Org
  data for convenience is what module boundaries exist to prevent.
- **A row cap, not a queue.** At most 5,000 rows per request, refused with a message rather than
  truncated. An import that takes four minutes needs a job, a progress endpoint and somewhere to keep
  its result — a different feature than this one.

### 6.1 Import formats still to come

CSV first because that is where this industry's data actually lives. The reader is a seam: coercion,
validation, the write and the rejected-rows file all work on parsed rows, so each format below adds a
reader and changes nothing else.

| Format | Media type | Notes |
|---|---|---|
| JSON | `application/json` | Arrives already typed, so it skips coercion entirely. |
| Excel | `.xlsx` media type | Also typed — a date cell is a date. Needs a library; **ClosedXML** (MIT), not EPPlus, which is no longer free for commercial use. |

The rejected-rows file is worth deciding per format when they land: returning CSV for an Excel upload
is defensible (every spreadsheet opens it) but is not the shape it was sent in.

### 6.2 The import screen (Week 5)

**Upload, check, apply** is built. Correcting a flagged cell in place — the editable grid below — is
the slice after it; until then the refused rows come back as a file to fix and re-send, which stays
regardless as the escape hatch for files too big to review by eye.

What the response already provided for it, and what the screen does with each:

- `accepted` / `rejected` / `imported` are **three separate numbers** because the screen has three
  different sentences to say: what is valid, what is wrong, and what is now in the database. After a
  dry run or a failed `AllOrNothing` run, `imported` is 0 while `accepted` is not.
- `problems` are structured `{row, column, message}` — a table to scroll and sort, not prose to parse
  apart. `row` is the line number in the uploaded file, header included, so it matches what the
  admin's spreadsheet shows.
- `rejectedRowsCsv` is a download button.
- `ignoredColumns` is the warning banner that catches a mistyped custom-field header.
- `OutletImportFormat.MaxRows` is public so the screen can refuse an oversized file before uploading
  it — which works for a C# caller and not at all for the one client that needs it. So the facts are
  also served: **`GET /api/outlets/import`** answers `{ maxRows, mediaTypes, reasonColumn }`, held to
  `outlet:write` like the import it describes. A front end hard-coding 5,000 would hold a second copy
  of a rule only the server enforces, and that copy drifts without anything failing — nothing breaks
  when the two disagree, the screen simply starts lying about the limit. `mediaTypes` is there for
  the same reason: the file picker widens on its own the day JSON and Excel land.

Two things the screen adds that the API did not have to:

- **Check and apply are two presses, and Apply is unavailable until Check has run.** The dry run
  costs nothing and returns exactly what the real run would, so there is no reason to offer a path
  that skips it — and "import this file I have not looked at" is the mode this endpoint deliberately
  does not have. Re-adding it on screen would put it back.
- **The file stays in the browser between the two calls** and is sent twice, rather than parked
  server-side behind a token. A synchronous import has no result to outlive its response, and keeping
  one would mean a table, a retention rule and a cleanup job for a file the admin already has open.
  It is also what the grid needs next: correcting a cell means re-serialising the file that is
  already here and checking it again.

**The review step is an editable grid.** Upload, dry-run, and the file comes back as a table with the
bad cells flagged — fix them in place, re-check, apply. Mistakes get corrected *before* anything is
written rather than after, which is the difference between an admin fixing a typo and an admin fixing
a typo that is now an outlet other people can already see.

That the grid needed no new API is the evidence the response shape was right: `problems` already
carry `{row, column, message}`, `row` already matches the file's own numbering, and re-checking is
the same dry run again. The grid holds the file in the browser the whole time and serializes back to
CSV once something has been edited.

**A dry run hands the file back as it read it** — `columns`, and a `rows` array of
`{ row, values }` with `values` aligned to `columns`. That is what makes the grid possible without a
second CSV reader.

The alternative was for the screen to parse the upload itself, and it is worth being explicit about
why that is wrong. The client would then be deciding, independently, which row is row 7 — a decision
the server has already made and reported in every `problems[].row`. Two readers agreeing about quoted
delimiters, embedded newlines, blank lines and record-versus-line counting is a standing obligation,
and the failure has no symptom: the grid flags a cell in the wrong shop, and someone corrects data
that was fine. The reader that numbered the problems is the one that should say what is in the row.

What the browser keeps is a **writer**, which cannot make that mistake — it emits the rows it was
given, in order. A first pass at this shipped the reader plus a guard comparing row counts, which
detected the disagreement instead of preventing it; removing the reader removed the need for the
guard.

Sent on dry runs only, and bounded by the row cap: a real run has nothing left to correct and the
caller is holding the rows already.

Five smaller things:

- **The grid shows the whole file, filtered by default to the rows with problems.** Showing only
  what failed hides the two things the good rows are evidence of: that the columns mapped the way the
  admin expected, and that a problem naming another row — a code duplicated on row 3 — can be read
  against that row. A filter narrows the view; it does not decide what exists. Past a hundred rows
  the grid stops and says what it is not showing, because 4,000 rows across eight columns is 32,000
  inputs and a browser asked to build them stops being a browser. `rejectedRowsCsv` is the answer at
  that size, which is what it is for.
- **Every row is checked, and unchecking one leaves it out.** A row that cannot be fixed today is
  otherwise a dead end: the only ways out are `Partial` mode, which decides for you, and editing the
  file outside the app. The two compose — the selection picks the set, the mode decides what happens
  to bad rows still in it. The opposite default, where an empty selection means everything, makes the
  box mean two things: the click that felt like adding one row would have dropped the other 3,999.
- **Unchecked rows are dropped from the file that is sent**, so the file that was checked is exactly
  the file that is applied. Consequence worth knowing: an exclusion renumbers the rows after it, so
  once someone has excluded a row the numbers stop matching the original spreadsheet. Acceptable
  because the grid is the reference by then, and preferable to a `skip=` parameter that would keep
  the numbering at the price of API surface that grows unusable past a few dozen rows.
- **Any change — a cell or a checkbox — makes Apply unavailable until the file is checked again.**
  Without it, correcting a cell and pressing Apply writes something nobody has looked at, which is
  the one thing this screen exists to prevent.
- **The uploaded bytes are sent untouched until something is changed.** "Check the file I gave you"
  should mean the file they gave us.

Deliberately **before** the write rather than after it:

- A post-apply grid would be editing outlets, which is the Outlets screen — the import has no more to
  say about them by then.
- Showing "what this import created" needs a batch identity, which needs a persisted import record —
  the thing an inline result deliberately avoids. If audit ever wants it ("who loaded these 4,000
  outlets"), that is a feature to add on purpose, not a side effect of a review screen.

`rejectedRowsCsv` stays regardless: it is the escape hatch for `Partial` runs, for files too big to
review by eye, and for anyone driving this from a script rather than a browser.

The flow is therefore **dry run, fix in the grid, apply** — which is why the dry run costs nothing and
returns exactly what the real run would.

## 7. Offline behavior

Outlets are **reference data**: pulled to the device (territory-scoped, [A4](decisions-and-assumptions.md#a4--offline-data-scope-territory-scoped))
and **read-only** on device. Rep corrections/new-outlet requests are captured offline as
**proposals** and pushed via the outbox; they never mutate master data directly (they enter the
review queue on the server). This keeps outlets conflict-free ([B7](decisions-and-assumptions.md#b7--conflict-resolution-matrix)).

## 8. Module contract (exposed to others)

- `IOutletCatalog` — resolve outlet by id; list by territory/channel; geofence, timezone,
  order-block flag.
- `IOutletClassification` — channel/segment of an outlet (used by Products, Journey, Audit).
- `IReferenceChangeFeed` (sync source) — territory-scoped, row-version delta of outlets with
  tombstones, for **Sync** ([module boundaries §7](../architecture/10-module-boundaries.md#7-module-registry)).
- `IOutletProposalIngest` — apply a pushed outlet **proposal** (correction / new-outlet request)
  into the review queue through this module, used by **Sync** (proposals never mutate master data
  directly, [§7](#7-offline-behavior)).
- Consumes `ITerritoryDirectory` (Organization) and `IFieldDefinitionCatalog` (Configuration —
  custom-field validation, BR-OUT-5).
- Publishes `OutletChanged`, `OutletClosed` → Journey/Sync react.

## 9. Acceptance criteria (sample)

- Saving an outlet without a channel is rejected.
- A rep's offline correction appears in the back-office review queue after sync and does not
  alter the outlet until approved.

## 10. Open questions

- Do banners/chains need modeling in v1, or defer? (Assumed: optional field, no chain-level
  logic in v1.)
- Approval SLA/roles for the review queue. (Assumed: any Sales Ops user.)

