# ADR-0012: Server messages are codes, not prose

- **Status:** Accepted
- **Date:** 2026-08
- **Deciders:** Tiberiu Socea
- **Related:** [ADR-0010](0010-internationalization.md) (which this extends),
  [ADR-0004](0004-nextjs-offline-first-frontend.md), [ADR-0007](0007-offline-sync-strategy.md),
  [API contracts §3](../13-api-contracts.md#3-error-model--rfc-7807-problem-details),
  decision [A3](../../product/decisions-and-assumptions.md#a3--internationalization-full-multi-currency--multi-language-ui)

## Context

[ADR-0010](0010-internationalization.md) decided the internationalization primitives: `Money`, UTC +
`IClock`, and `next-intl` message catalogs for the UI. It settled how the **front end** says things.
It never said how the **server** says things, and the server says a lot.

Today every refusal the API produces is English prose written at the call site:

```csharp
Problems.Conflict("name", $"A channel named '{request.Name}' already exists.");
Problems.BadRequest($"A contact's {what} is at most {max} characters.");
```

There are **62 such call sites across 10 files in 4 modules**, plus 3 more that build a
`FieldProblem` directly in a validator. The front end renders them verbatim —
`form.setError(problem.field, { message: problem.message })` in nine form components — so a user on
`/ro` fills in a Romanian form and gets an English validation error under the control. The i18n
story has a hole in it exactly where a user is most likely to be paying attention.

This has been survivable because the surface is small and the languages are two. **W6 (Products &
Pricing) is where it stops being survivable**: pricing and assortment rules are refusal-heavy by
nature — no price list for this currency, promotion not active on this date, SKU not in this outlet's
assortment — and they arrive as a module's worth of new messages at once. Deciding after W6 means
migrating a larger surface and writing a module's messages twice.

Two properties of this project narrow the choice more than they would elsewhere.

**ADR-0010 made a promise.** It states as a consequence that *"adding a language is a **content**
task (a catalog), not an engineering change — and that claim is enforced, not asserted."* Server-side
prose breaks that promise: a third language would mean touching the back end, in a second catalog
system, with a second parity gate.

**The app is offline-first** ([ADR-0004](0004-nextjs-offline-first-frontend.md),
[ADR-0007](0007-offline-sync-strategy.md)). A message rendered on the server is prose frozen at the
moment it was minted. When the W8 sync engine replays a queued push, any refusal it carries was
localized whenever the server happened to produce it — possibly on a device that has since changed
language, possibly for a user who never saw the request go out. A locale switch cannot re-render
prose that has already been written down.

## Decision

**The server names the problem; the client says it.**

A refusal carries a **stable message code** and its **arguments**, plus the current English prose as
a **fallback**:

```json
{
  "errors": [
    {
      "field": "name",
      "code": "channel.name.taken",
      "args": { "name": "Modern Trade" },
      "message": "A channel named 'Modern Trade' already exists."
    }
  ]
}
```

- **`code`** is a stable, dotted, resource-first identifier — `channel.name.taken`,
  `outlet.customField.tooLong`. It is part of the API contract: renaming one is a breaking change,
  the same as renaming a field.
- **`args`** carries the values the message interpolates, named. Codes alone are not enough here —
  more than half the existing messages interpolate something, and `"at most {max} characters"`
  without `max` is not a message.
- **`message`** stays, as **English fallback and nothing more**. It is what a client that does not
  know a code shows, what a `.http` response reads as, and what an integration test asserts on. It is
  explicitly *not* the localized string, and a client that renders it to a user who chose Romanian is
  using it wrong.
- **Resolution is client-side**, through the `next-intl` catalogs that already exist and already have
  a [catalog-parity test](0010-internationalization.md) that fails the build on drift. One catalog
  system, one gate, one place a language is added.

`field` is unchanged and keeps its meaning from
[API contracts §3](../13-api-contracts.md#3-error-model--rfc-7807-problem-details) — the JSON path
the caller sent. This ADR adds to that envelope; it does not reshape it.

## Options considered

| Option | Verdict | Why |
|---|---|---|
| Leave it English-only | Rejected | Already visibly wrong in the `/ro` UI, and the surface grows by a whole module in W6. Deferring makes the same decision cost more. |
| `Accept-Language` negotiated server-side | **Reasonable, rejected** | Smallest client change, and the obvious answer for a conventional web API. But it needs a second catalog system in the back end, it makes adding a language an engineering change (contradicting ADR-0010's own stated consequence), and it freezes language at mint time — which offline replay cannot undo. |
| Codes + args, no `message` | Rejected | Cleanest envelope, but an unknown code has nothing to show, `.http` responses go opaque, and every one of the 62 sites must land with its catalog entry in the same change. The fallback costs one string and buys incrementality. |
| **Codes + args + English fallback** | **Chosen** | Keeps one catalog system and one parity gate; survives offline replay and locale switches; additive, so it can land module by module without a flag day. |

## Consequences

**Positive**
- A language is added by writing a catalog, on the front end, once — the ADR-0010 promise holds for
  server messages too rather than only for UI chrome.
- A refusal stored offline renders in whatever language the device is in **when it is read**, not
  when it was created. This is the property `Accept-Language` cannot provide, and it is the one that
  matters most for the field app.
- The server stops owning presentation. `"A channel named 'X' already exists."` is a sentence; the
  server's actual job is to say *which rule* was broken and *about what*.
- Codes are greppable and stable, which makes them usable for support and analytics — "how often does
  `outlet.import.headerMissing` fire?" is a question prose cannot answer.

**Negative / costs**
- **62 call sites to migrate**, plus catalog entries in `en` and `ro` for each. Staged below; not a
  flag day, but real work.
- **A code is now API surface.** Renaming one breaks clients, so they need naming discipline up front
  — hence resource-first dotted names, mirroring the permission strings' `resource:action` shape.
- **A new server rule needs a client catalog entry**, or the user sees the English fallback. That
  coupling is real. It is mitigated by both living in this repo and by the fallback being a correct
  sentence rather than a raw code — a missing translation degrades to English, not to
  `channel.name.taken`.
- **Two things to keep aligned per message** (the code's args and the catalog's placeholders). The
  parity test covers catalog-to-catalog drift; it does not cover server-to-catalog drift. Closing
  that needs a test that walks the codes the server can emit — worth doing, and not in the first
  slice.

## Implementation (staged)

This ADR is the decision; the migration is deliberately not one change.

| Stage | Scope |
|---|---|
| 1 | `FieldProblem` grows `Code` and `Args`; `Problems.*` overloads accept them. Additive — existing call sites compile unchanged and keep emitting `message` only. |
| 2 | Client resolves `code` + `args` through `next-intl`, falling back to `message` when the code is unknown. Nothing breaks while codes are absent. |
| 3 | Migrate module by module, one PR each: Org (29), Outlets (21, of which 4 are the CSV import), Iam (10), Configuration (2) — 62 in total, plus the 3 validator-built problems. Org is largest and Configuration smallest, so start at the small end and let the shape prove itself on something cheap to redo. |
| 4 | A test enumerating emittable codes against both catalogs, closing the server-to-catalog drift gap. |

**W6 writes codes from the start.** Products & Pricing is the reason this was decided now; its
messages should not be written in the old shape and migrated a month later.

Until a module is migrated, its refusals carry `message` only and the client falls back to English —
the behaviour that exists today, unchanged. **Nothing in this ADR is implemented yet**; the envelope
in [API contracts §3](../13-api-contracts.md#3-error-model--rfc-7807-problem-details) describes what
ships today, with the target shape marked as such.
