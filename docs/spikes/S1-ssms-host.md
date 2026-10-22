# Spike S1 — SSMS host integration (protocol)

- Status: **In progress — step 1 passed on SSMS 22** (prototype in [`spikes/S1.SsmsHost`](../../spikes/S1.SsmsHost); VSIX built by CI)
- Decision informed: [ADR-0005](../adr/0005-first-product-surface.md) — SSMS first only if this spike is clean
- Time-box: 5 working days

## Question

Can a thin SSMS adapter host the engine reliably on every SSMS version we would support, given that Microsoft does not officially support third-party SSMS extensions?

## Environment matrix

Record exact versions; run every step on each row.

| Row | SSMS version (Help → About) | Windows | Notes |
|---|---|---|---|
| A | 21.x | Windows 11 x64 | Not tested (developer machine has 22 only) |
| B | 22.10.12210.168 | Windows 11 x64 | Developer machine; VS 2026 shell |
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

## Evidence

### Step 1 — Hello SSMS (2026-10-22, row B: SSMS 22)

| Fact | Value | Source |
|---|---|---|
| Install | Double-click VSIX → VSIXInstaller offered SSMS 22 → "completed successfully"; per-user (`PerMachine: False`) under `%LOCALAPPDATA%\Microsoft\SSMS\22.0_*\Extensions` | VSIXInstaller log |
| Registration | Listed in Options → Environment → Extensions (per-extension auto-update setting available) | Screenshot |
| SSMS version | 22.10.12210.168 | `s1-host.log` |
| Shell | `DTE.Version=18.0` — **Visual Studio 2026 shell**. An extension built against SDK 17.14 (VS 2022) loads unchanged via the VS 2026 compatibility model | `s1-host.log` |
| Runtime | .NET Framework 4.8.9363, x64 — the .NET 10 engine must run out of process | `s1-host.log` |
| Load | Background auto-load ≈ 5 s after start; no start-up delay | `s1-host.log` |
| Query editor content type | `SQL` (base chain `code > text > any`); roles include `DOCUMENT, EDITABLE, INTERACTIVE, PRIMARYDOCUMENT` | `s1-host.log` |
| Document identity | Unsaved query windows have a temp path (`%TEMP%\SQLQuery1.sql`) | `s1-host.log` |
| Events | Text-change and caret events received | `s1-host.log` |
| Built-in features | SSMS ships its own completion and formatter as extensions (`SSMS.SqlCompletions`, `SSMS.SqlFormatter`) | VSIXInstaller log |

| Criterion | Status |
|---|---|
| 1 Install / upgrade / uninstall | Install ✅ · upgrade and uninstall pending |
| 2 Text and caret access | Read ✅ · write pending |
| 3 Completion coexistence | Step 3 |
| 4 Commands / key bindings | Command registered ✅ · invocation and key bindings pending |
| 5 Out-of-process worker | Step 2 |

Risk noted: the extension relies on VS 2026's compatibility layer for 17.x extensions; a future SSMS could require rebuilding against SDK 18.x.

## Decision rule

- **Clean** — steps 1–5 pass on rows A and B, and step 6 needs no non-public API: accept ADR-0005 with **SSMS first**.
- **Otherwise** — accept ADR-0005 with **standalone first**; record failing steps as Phase 4 risks for the SSMS adapter.

## Deliverables

- `spikes/S1.SsmsHost/` — the throwaway VSIX (not shipped).
- This document updated with the evidence table per row and the decision.
- ADR-0005 status changed to Accepted.
