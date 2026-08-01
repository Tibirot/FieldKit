# ADR-0001: Record architecture decisions

- **Status:** Accepted
- **Date:** 2026-07
- **Deciders:** Tiberiu Socea

## Context

FieldKit is built to demonstrate architectural judgement, not just working code. Decisions
like "modular monolith over microservices" or "server-authoritative sync over CRDTs" are
only valuable if the *reasoning* is visible and durable. Reasoning that lives only in a
developer's head, a chat log, or a commit message is lost the moment the project is read by
someone else — which, for a portfolio, is the entire audience.

## Decision

We will record every significant architectural decision as an **Architecture Decision
Record (ADR)** in `docs/architecture/adr/`, using Michael Nygard's template: **Context →
Decision → Consequences**, plus the options we rejected and why.

ADRs are immutable. A decision that is later reversed is not edited; a new ADR is added that
supersedes it, and the old one is marked `Superseded by ADR-XXXX`. The index in
[README.md](README.md) lists all ADRs and their status.

A decision is "significant" — and thus gets an ADR — if it is expensive to reverse, affects
module boundaries or the tech stack, or is something a reviewer would reasonably ask "why?"
about.

## Consequences

**Positive**
- The "why" travels with the code and survives context loss.
- Reviewers can evaluate judgement, not just implementation.
- Forces the trade-offs to be made explicitly rather than by drift.

**Negative / costs**
- A small writing tax on each real decision. Accepted deliberately — for this project the
  documentation *is* a deliverable.

**Neutral**
- ADRs describe decisions, not current state. When implementation and an ADR diverge, that's
  a signal to write the *next* ADR, not to silently edit the old one.
