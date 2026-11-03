# ADR-0005: First product surface

- Status: **Accepted** (2026-11-03) — SSMS first, SSMS 22 and later
- Date: 2026-10-22 (proposed), 2026-11-03 (accepted)

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

## Decision (2026-11-03)

**SSMS adapter first, targeting SSMS 22 and later only.** Spike S1 met all seven criteria on SSMS 22.11
([evidence](../spikes/S1-ssms-host.md#outcome-row-b)). SSMS 21 is out of scope rather than untested: it runs on the
VS 2022 shell, SSMS 22 on the VS 2026 shell, so evidence from one does not transfer to the other, and the primary
user works in SSMS 22. The "every target version" condition is met by narrowing the targets.

Consequences of the narrowing:

- The VSIX declares `InstallationTarget Microsoft.VisualStudio.Ssms [22.0,23.0)`; SSMS 21 will not offer to install it.
- Phase 3 is **3a, the SSMS adapter**; the standalone application becomes the Phase 4 second surface.
- Each new SSMS major (23+) is a re-run of the S1 protocol before widening the range.

Risks carried into Phase 3a:

| Risk | Mitigation |
|---|---|
| SSMS-private assemblies move between releases (`ServiceCache` moved to `SqlPackageBase.dll`) | One connection adapter, late-bound by full type name; start-up self-test logged per SSMS build; editor features never depend on it |
| Extension built on SDK 17.x runs via VS 2026's compatibility layer | Track SSMS previews; rebuild against SDK 18.x when SSMS requires it |
| Connection info follows the active window, not the document (S1 finding F4) | Resolve and cache per query document on activation |
| No supported extensibility contract for SSMS | Thin adapter over the LSP engine (ADR-0001), so a breaking SSMS change costs adapter work only |

