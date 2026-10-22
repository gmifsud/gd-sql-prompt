# Spike S1 — SSMS host integration (protocol)

- Status: **Ready to run — requires a Windows machine with SSMS** (cannot run in the Linux CI/cloud environment)
- Decision informed: [ADR-0005](../adr/0005-first-product-surface.md) — SSMS first only if this spike is clean
- Time-box: 5 working days

## Question

Can a thin SSMS adapter host the engine reliably on every SSMS version we would support, given that Microsoft does not officially support third-party SSMS extensions?

## Environment matrix

Record exact versions; run every step on each row.

| Row | SSMS version (Help → About) | Windows | Notes |
|---|---|---|---|
| A | 21.x (current GA) | Windows 11 x64 | Clean VM snapshot |
| B | 22.x (current GA, if released) | Windows 11 x64 | Clean VM snapshot |
| C | Preview channel (optional) | Windows 11 x64 | Early warning only; not a gate |

Facts to establish and record before coding (verify against the installed product, not documentation memory):

1. Visual Studio shell version each SSMS build is based on (`devenv`-equivalent `Ssms.exe` file version, `Common7\IDE` layout).
2. Supported extension deployment mechanism: VSIX installer targeting the SSMS instance, or file drop into the extensions folder.
3. The content type and text-view roles of the query editor (inspect via an MEF `IWpfTextViewCreationListener` logging `TextView.TextBuffer.ContentType`).

## Steps and pass criteria

Each step maps to an ADR-0005 criterion. Capture evidence (screen recording or log excerpt) per row.

| # | Step | Pass when | ADR-0005 |
|---|---|---|---|
| 1 | Package a minimal VSIX (MEF text-view listener + one command). Install, restart, upgrade to v2, uninstall. | No manual file edits; SSMS starts cleanly after each operation; uninstall leaves no load errors (`ActivityLog.xml`). | 1 |
| 2 | On query-window open, log buffer content type and read/replace a selection via `ITextBuffer` edit. Track caret moves. | Text and caret available for every query window, including after reconnect and "New Query" from Object Explorer. | 2 |
| 3 | Contribute an `IAsyncCompletionSource` (keywords from the engine). Test with SSMS IntelliSense on and off. | Our items appear; with SSMS IntelliSense off there is no duplicate session; with it on, behaviour is defined (merge or suppress) and stable. | 3 |
| 4 | Register commands and default key bindings; rebind via Tools → Options → Keyboard. | Commands persist across restarts; no collisions with SSMS defaults we rely on. | 4 |
| 5 | Start `sqlwc-lsp` as a child process over stdio from the extension; kill it externally while typing; close SSMS. | Typing never blocks; worker restarts within 2 s with documents resynchronised; no orphaned process after SSMS exits. | 5 |
| 6 | Obtain the active connection (server, database, auth kind) for the focused query window. | Available without reflection on non-public types — or the dependency on internals is documented with its blast radius. | Risk input |
| 7 | Measure keystroke → completion list latency with a 5,000-line script open. | p95 ≤ 150 ms warm (spec §13), measured with ETW/PerfView or a stopwatch log. | Risk input |

## Decision rule

- **Clean** — steps 1–5 pass on rows A and B, and step 6 needs no non-public API: accept ADR-0005 with **SSMS first**.
- **Otherwise** — accept ADR-0005 with **standalone first**; record failing steps as Phase 4 risks for the SSMS adapter.

## Deliverables

- `spikes/S1.SsmsHost/` — the throwaway VSIX (not shipped).
- This document updated with the evidence table per row and the decision.
- ADR-0005 status changed to Accepted.
