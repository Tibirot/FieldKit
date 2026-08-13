# Full regression — 13 August 2026, after W11

A whole-app pass at the end of Week 11, run against `main` at
[`16d52ca`](https://github.com/Tibirot/FieldKit/commit/16d52ca) — every automated gate, plus static
analysis and a browser sweep looking for the things a green suite cannot tell you.

**Headline: every gate is green and nothing found is a bug in shipped behaviour.** The findings are
six gaps — a doc that has drifted, a stored value nothing reads, a rule mirrored in two languages with
no shared corpus, three latent test flakes, and one small usability limit. They are ordered by what
they would cost if left.

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

**Coverage this pass did not have.** The browser sweep ran as an administrator only, so the field
app's rep-side flows — check-in, the audit screen, order capture, sync — were exercised by their unit
and integration tests but not by hand. The `/en/field` shell renders and syncs; the flows behind it
were last walked by hand during W11 slices 9–14.

---

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

1. **F4** — three lines, removes a known CI flake.
2. **F2** — one table row.
3. **F1** — the only finding that costs a rep something today.
4. **F3** — a vector file and two readers; prevents a silent divergence.
5. **F5** — one line of JSX.
6. **F6** — with the `OutletSnapshot` time-zone debt, not before.

None blocks W12.
