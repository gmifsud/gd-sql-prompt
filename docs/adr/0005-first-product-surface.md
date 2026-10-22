# ADR-0005: First product surface

- Status: Proposed — to be accepted at the Phase 0 exit gate
- Date: 2026-10-22

## Context

SSMS is where SQL Prompt users work, but Microsoft does not officially support third-party SSMS extensions. A standalone application gives full control but carries connection, execution and results scope (spec §11).

## Proposed decision

**Standalone application first**, unless spike S1 demonstrates a stable SSMS integration across all target SSMS versions, in which case the SSMS adapter goes first.

S1 counts as "clean" only if all of the following hold on every target SSMS version:

1. Install, upgrade and uninstall of the VSIX succeed without manual steps.
2. Text buffer read/write and caret tracking work in the query editor.
3. The completion session can be supplied or replaced without conflicting with built-in IntelliSense.
4. Commands and key bindings register reliably.
5. An out-of-process worker can be started, restarted after a crash, and stopped with the host.

## Consequences

- Standalone first: Phase 3 includes APP P0 items, history and environment cues; SSMS follows in Phase 4 as the second surface.
- SSMS first: Phase 3 is the adapter; the standalone app becomes the Phase 4 second surface.
