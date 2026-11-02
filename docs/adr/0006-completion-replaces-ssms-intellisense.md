# ADR-0006: SQL Companion completion replaces SSMS IntelliSense in query windows

- Status: Accepted
- Date: 2026-11-02
- Evidence: [S1 step 3](../spikes/S1-ssms-host.md#step-3--completion-and-step-2-confirmation-2026-11-02-ssms-2211-vsix-030)

## Context

S1 step 3 showed that SSMS's T-SQL IntelliSense is a classic language service reached through the editor's legacy
completion shims; it registers no modern (async) completion provider. With both active, the two systems contend
for keystrokes — SSMS consumes `@`, our list goes stale, and neither offers `@Total` — which is worse than either
alone. With SSMS IntelliSense off, our completion was complete, correct and fast (p95 45 ms).

## Decision

1. In SSMS query windows, SQL Companion completion **replaces** SSMS IntelliSense completion when enabled.
   One visible setting controls it.
2. SQL Companion switches SSMS IntelliSense off and back on only through SSMS's own command
   (`Query.IntelliSenseEnabled`), and restores it only if SQL Companion was the one that switched it off.
   Private SSMS settings are never edited.
3. The default stays **off** (SSMS IntelliSense only) until SQL Companion completion is schema-aware
   (Phase 2, metadata spike S3), so installing SQL Companion never makes completion worse.

## Consequences

- Switching SSMS IntelliSense off also removes its quick info, parameter help and syntax-error underlining.
  Before Replace becomes the default, SQL Companion must reach parity on: object and column completion
  (CMP-002, CMP-003), function and procedure assistance (CMP-005, CMP-006) and syntax diagnostics (ANA-001).
  Track these in the [traceability matrix](../traceability-matrix.md).
- Completion ranking, context and keyboard behaviour are entirely ours, so behaviour is consistent across
  SSMS and the other surfaces (ADR-0001).
- The S1 merge experiment is kept as evidence only; it does not change this decision.

## Alternatives considered

| Option | Not chosen because |
|---|---|
| Merge into SSMS's list (extra tab via legacy shims) | Two engines disagree on context and ranking; depends further on SSMS internals |
| Separate trigger (shortcut only) | Safe, but not the inline experience the specification targets |
