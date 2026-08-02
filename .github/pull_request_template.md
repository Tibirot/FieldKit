<!--
FieldKit PR. Rules: docs/engineering/pull-requests.md
Keep it small and single-purpose. Green CI before marking ready. Docs change with code.
-->

## What & why
<!-- One or two sentences: what this changes and the motivation. -->

## Scope
- **Implements / fixes (spec IDs):** <!-- e.g. VIS-01, VIS-02, ORD-12 -->
- **Module(s):** <!-- e.g. Visit -->
- **Delivery-plan week / phase:** <!-- e.g. W7 / Phase 2 -->
- **Not in this PR:** <!-- what a reviewer might expect but is deliberately excluded/stacked -->

## How it was tested
<!-- Which tests, and evidence the gates pass. For a bug fix, note the regression test that fails before the fix. -->
- [ ] Unit
- [ ] Integration (Testcontainers / real Postgres)
- [ ] Architecture tests green
- [ ] Tenant-isolation test (if data access)
- [ ] Sync property tests (if offline/sync paths)
- [ ] Pricing/score parity vectors (if those engines)



## Docs updated (in this PR)
<!-- Docs move with code. List the spec / ADR / module-registry entries you changed, or state why none apply. -->

## UI (if user-facing)
<!-- Before/after screenshots or a short GIF. Compare against docs/ux/README.md. -->

## Risk & rollback
- **Blast radius:**
- **How to revert:**
- **Migration:** <!-- none | expand→contract, reversible -->
- **Feature flag:** <!-- none | flag name, default off -->

## Reviewer notes
<!-- Where to start reading; anything non-obvious. Self-reviewed with inline comments? -->

---
### Definition of Done
- [ ] One purpose; within the size budget (or cleanly stacked)
- [ ] Behavior changes covered by tests; bug fix has a failing-first regression test
- [ ] Architecture-test + tenant-isolation gates green; no banned patterns (`IgnoreQueryFilters`, `DateTime.Now`, raw tenant-bypass)
- [ ] Owning spec / ADR / registry updated here (docs-in-lockstep)
- [ ] Spec IDs + week cited; scope and non-scope stated
- [ ] Migration reversible / expand-contract; risky work behind a flag
- [ ] Self-reviewed with inline notes; CI green; draft → ready
- [ ] No secrets, no unrelated files, no gate weakened to pass

<!-- Agents: never merge/approve; never force-push main; never bypass hooks. Stop and ask on auth/tenancy, public-contract, destructive migration, or secrets/infra changes. -->
