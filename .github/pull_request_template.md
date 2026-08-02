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

## Pre-PR agent review
<!-- Agent-authored PRs only; delete if a human wrote the change AND no agent pushed to it later —
     an agent's pushes to a human's PR are recorded here too. Rules: §8 of
     docs/engineering/pull-requests.md. Paste each round's COMPLETE, UNEDITED output — a paraphrase
     can't be checked, and a verbatim subset is still cherry-picking. Every round stays, including
     ones whose findings were later fixed. "No findings" is valid but weak evidence; say so plainly.
     Not an approval; the human's review is unchanged. -->
- **Reviewed by:** <!-- e.g. Fable, over `git diff <base>...HEAD`, before this PR was opened -->
- **Brief given:** <!-- summarise, or link the prompt — independence depends on it -->

<details><summary>Round 1 — complete output</summary>

<!-- paste unedited -->

</details>

**Disposition (round 1):** <!-- per finding: fixed (how) / stands (why) -->

<!-- Repeat the block above for each follow-up round; if one ran, the final round covers the full
     diff. If you judged your fixes immaterial and ran no follow-up, say so here. Too large for the
     body? Post it as a PR comment and link it — move it, don't truncate it. -->


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
- [ ] Independent frontier-model review done **before** opening; verbatim findings + dispositions above (if agent-authored)
- [ ] Self-reviewed with inline notes; CI green; draft → ready
- [ ] No secrets, no unrelated files, no gate weakened to pass

<!-- Agents: never merge/approve; never force-push main; never bypass hooks. Stop and ask on auth/tenancy, public-contract, destructive migration, or secrets/infra changes. -->
