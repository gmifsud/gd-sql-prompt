# ADR-0003: Engine-first phasing

- Status: Accepted
- Date: 2026-10-22

## Context

Spec §15 bundles each phase with a product surface. The highest technical risk is in tolerant parsing, binding and metadata scale, not in UI.

## Decision

Deliver in the order: Phase 0 spikes → engine core + CLI → metadata/binder/completion over LSP → first product surface → parity → advanced (see [roadmap](../roadmap.md)). Each phase has a measurable exit gate.

## Consequences

- Earliest shippable value is the CLI (CI format/lint checks), which needs no database or host.
- Surface work starts against a stable engine API, reducing adapter churn.
- A VS Code LSP client is used early as a development harness, independent of its later release priority.
