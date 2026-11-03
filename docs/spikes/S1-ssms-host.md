# Spike S1 — SSMS host integration (protocol)

- Status: **Complete on row B (SSMS 22.11) — all seven criteria met.** Row A (SSMS 21) not run: no SSMS 21 available. Decision pending at the Phase 0 gate (see *Outcome*). Prototype in [`spikes/S1.SsmsHost`](../../spikes/S1.SsmsHost); VSIX built by CI
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

### Step 2 — Out-of-process engine (2026-10-24, row B: SSMS 22.11)

| Fact | Value | Source |
|---|---|---|
| Upgrade | 0.2.0 installed over 0.1.0: the engine runs from a new extension folder (`…\Extensions\30x2mb51.gsn\`, was `oyk2hyfp.ilm`) | `s1-host.log` |
| SSMS self-update | SSMS updated 22.10.12210.168 → **22.11.12315.225** between sessions; the extension kept loading without reinstall | `s1-host.log` |
| Engine start | `sqlwc-lsp.exe` (.NET 10) started from the VSIX `server\` folder 93 ms after the first query window opened | `s1-host.log` |
| Document lifecycle | Open/close per query window forwarded to the engine | `s1-host.log` |
| Not yet evidenced | Squiggles (the test text had no engine-detectable error), crash recovery, orphan check, latency. The 0.2.0 log did not record engine readiness or diagnostics; 0.3.0 adds both | — |
| SSMS IntelliSense | Active and schema-aware (`dbo.` lists tables); its own parse errors appear in the Error List | Screenshot |

| Criterion | Status |
|---|---|
| 1 Install / upgrade / uninstall | Install ✅ · upgrade ✅ · survives SSMS self-update ✅ · uninstall pending |
| 5 Out-of-process worker | Starts from VSIX ✅ · diagnostics round trip, crash recovery and orphan check pending (CI-verified on Windows against the real engine) |

### Step 3 — Completion and step 2 confirmation (2026-11-02, SSMS 22.11, VSIX 0.3.0)

| Fact | Value | Source |
|---|---|---|
| Completion providers serving `SQL` editors | Only ours is SQL-specific. SSMS's T-SQL IntelliSense registers **no** MEF completion provider; the editor reaches it through the generic legacy shims (`ShimCompletionSourceProvider`, `ShimCompletionControllerFactory`, content type `code`) — i.e. a classic language service, not modern async completion (inference from the provider list) | Provider report |
| Our async completion | Called by the editor (`GetOrCreate`, `InitializeCompletion` on insertion, backspace, deletion); items shown with the *SQL Companion* suffix and icons | Log, screenshots |
| Completion latency | p50 2 ms, p95 45 ms, max 45 ms (first request, cold) over 17 requests — target ≤ 150 ms ✅ | Diagnostics dialog |
| Coexistence | IntelliSense **off**: ours complete and correct (`@Total` offered after `SELECT @`). IntelliSense **on**: SSMS consumes `@` and our list goes stale; neither list offers `@Total`. The two systems do not merge | Screenshots |
| Squiggles | `SQLC0001` raised and cleared 4× while typing; again after a restart | Log |
| Crash recovery | Engine ended twice from Task Manager → restarted in **598 ms** and **726 ms**, 1 document resynchronised, diagnostics resumed | Log |
| Shutdown | `Engine stopped` on every SSMS close (10 sessions) | Log |
| Engine round trips | p50 1 ms, p95 13 ms, max 929 ms (first after start) over 101 | Diagnostics dialog |
| Soak | VSIX 0.2.0 in daily use 24 Oct – 1 Nov: ≈ 9 SSMS sessions, 20+ query windows, no probe or engine errors | Log |
| Upgrade | 0.2.0 → 0.3.0 installed to a new folder (`mgr3oack.lyq`) | Log |

| Criterion | Status |
|---|---|
| 1 Install / upgrade / uninstall | Install ✅ · upgrade ✅ (×2) · survives SSMS self-update ✅ · uninstall pending |
| 2 Text and caret access | Read ✅ · write pending |
| 3 Completion coexistence | ⚠️ Works alone; with SSMS IntelliSense on, the systems contend for keystrokes and do not merge. **Decided: replace** ([ADR-0006](../adr/0006-completion-replaces-ssms-intellisense.md)), applied per window (0.5.0, start-up gap fixed in 0.5.1); merge proven feasible via the legacy path (0.4.0) |
| 4 Commands / key bindings | Command ✅ · key bindings pending |
| 5 Out-of-process worker | ✅ restart < 1 s, resync, clean shutdown, multi-day soak |
| 7 Completion latency | ✅ p95 45 ms |

### Step 3b — Completion mode (2026-11-02, SSMS 22.11, VSIX 0.4.0)

| Fact | Value | Source |
|---|---|---|
| Toggle | `Query.IntelliSenseEnabled` exists; its latched state is readable and executing it switches SSMS IntelliSense (`before=on after=off`) | Log |
| **Scope of the toggle** | **Per query window, not global.** Window 1 off, window 2 (opened afterwards) still on — SSMS's own diagnostics ("Must declare the scalar variable") and the stale-list contention reappeared there | Screenshot, log (`before=on` read from window 2) |
| **Merge feasibility** | **Possible via the legacy path.** SSMS's session calls our legacy `ICompletionSource` (`legacy AugmentCompletionSession … 1 existing set(s)`), and our items appear as a **SQL Companion** tab beside SSMS's **All** tab in its own list | Log, screenshot |
| Defect found | 0.4.0 tracked a single global "we switched it off" flag: leaving Replace from window 2 did not restore window 1. 0.5.0 applies the mode per window on activation and restores only windows it switched off | Analysis |

Refined criterion 3: Replace works per window once applied on activation (0.5.0 to confirm); Merge is a proven
fallback.

### Step 3c — Per-window completion mode (2026-11-02, SSMS 22.11, VSIX 0.5.0)

| Fact | Value | Source |
|---|---|---|
| Replace, per window | Every query window activated in Replace was switched off on activation (`SQLQuery1`–`5`, two SSMS sessions), including windows opened after the mode change | Log |
| Persisted mode | Replace survived an SSMS restart and was applied to the new session's windows | Log |
| Leaving Replace | Merge restored only the active window at once, the others on their next activation (`SQLQuery5`, `4`, `3`) | Log |
| Merge | SSMS list with an extra **SQL Companion** tab; our tab lists `@asddd` and keywords | Screenshots |
| Latency | Completion p50 3 ms, p95 118 ms (cold first request) over 12; round trips p95 16 ms | Diagnostics dialog |
| **Defect D1** | While a window has no connection (start-up before connecting, or disconnected), `Query.IntelliSenseEnabled` reports **not enabled**, so the state is unknown and 0.5.0 did nothing. The window kept SSMS IntelliSense on until next activated — **75 min** in one session (18:08:23 → 19:23:00) | Log |
| **Defect D2** | Windows closed while switched off stayed in the controller's set (`SQLQuery1/2` listed after both were closed) — stale diagnostics, and a later window with the same path could be "restored" against the user's choice | Diagnostics dialog, log |

0.5.1 fixes both: an unavailable toggle is re-checked every second while that window stays the active document
(cancelled by any newer apply or a document switch), and `DocumentClosing` drops the window from the set. The
retry polls the public command state only; the alternative — a connection-changed hook — needs SSMS internals and
is deferred to step 6.

| Criterion | Status |
|---|---|
| 3 Completion coexistence | Replace per window ✅ (start-up retry kept, unexercised) · Merge feasible ✅ |

### Step 3d — 0.5.1 (2026-11-03, SSMS 22.11)

| Fact | Value | Source |
|---|---|---|
| D2 fixed | `SQLQuery1/3.sql: closed; forgotten`; diagnostics list only open windows | Log |
| D1 | Not exercised: SSMS started in Merge, so no window needed a retry | Log |
| **Defect D3** | Replace, typing `DECLARE @FF INT;` ⏎ `SELECT @` without pausing → list showed only `SELECT`; intermittently correct on a new tab | Screenshot |

D3 root cause (mechanism; confirmed by the step 3e test): no `IAsyncCompletionCommitManager` was exported for
`SQL`, so our sessions had no commit characters and outlived the word they started on. The session opened at `S`
kept extending its applicable span through the space and `@`; the item manager's pattern matcher drops the
non-word `@`, so filter text `SELECT @` matched only `SELECT`. `@FF` was returned but filtered out. Intermittency =
whether a pause/Esc ended the first session before `@`. 0.5.2 adds a commit manager: word-ending characters close
our session without inserting the selection (no `t.` → `TABLE.`), Tab/Enter commit. SSMS-style commit-on-space is
deferred to Phase 2 as a configurable behaviour.

### Step 3e — 0.5.2 (2026-11-03, SSMS 22.11)

| Fact | Value | Source |
|---|---|---|
| Commit manager | Ours registered for `SQL` beside the debugger's (`code`); word-ending characters end our session (`session ended by a word-ending character`), each followed by a fresh request | Provider report, log |
| D3 | No recurrence over 26 requests across 3 windows; p50 2 ms, p95 3 ms, max 44 ms (cold) | Diagnostics dialog, log |
| Start-up in Replace | Both start-up windows switched off on first activation (≈ 3 s after load); toggle available, so the 0.5.1 retry was not needed | Log |
| D1 retry | Still unexercised: the unavailable state (18:08 session) did not recur. Kept; low risk (bounded to the active window, public command state only) | Log |

Criterion 3 met: Replace per window, stable across restarts, closes cleanly; Merge proven feasible.

### Step 6 — Active connection (VSIX 0.6.0, protocol)

No SSMS SDK exposes the connection. Candidate route (long used by SSMS add-ins):
`ServiceCache.ScriptFactory.CurrentlyActiveWndConnectionInfo.UIConnectionInfo` in SSMS's
`Microsoft.SqlServer.Management.UI.VSIntegration`. The assembly is not redistributable and not on CI, so the probe
late-binds and records, per hop, whether member and type are public. Open questions the run answers:

1. Is every hop public (→ criterion met with "unsupported but public" risk) or not (→ blast radius)?
2. `AdvancedOptions[DATABASE]`: initial database only, or does it follow the drop-down / `USE`? If initial only,
   the current database needs another source (lead: the window's automation object, also probed) or our own
   tracking of `USE` plus a `DB_NAME()` query on the shared connection — to be decided from the evidence.
3. `AuthenticationType` values for Windows / SQL / Entra.
4. Does `ScriptFactory` expose a public connection-changed event (would replace the 0.5.1 polling)?

### Step 6 — Results (2026-11-03, SSMS 22.11, VSIX 0.6.0)

| Fact | Value | Source |
|---|---|---|
| Route | `ServiceCache.ScriptFactory.CurrentlyActiveWndConnectionInfo.UIConnectionInfo` — **every hop public** (members and declared/runtime types) | Diagnostics dialog |
| Assembly | `ServiceCache` now lives in **`SqlPackageBase.dll` 22.200.0.0**, not `Microsoft.SqlServer.Management.UI.VSIntegration.dll` — same namespace, moved assembly. Binding by full type name across loaded assemblies absorbed it; a compile-time reference would have broken | Diagnostics dialog |
| Server / user | `ServerName`, `UserName` match the status bar (Windows and SQL auth; local and remote server) | Diagnostics dialog |
| Auth kind | `AuthenticationType`: **0 = Windows**, **1 = SQL**; Entra not yet observed | Diagnostics dialog |
| Database | `AdvancedOptions[DATABASE]` is the window's **current** database: it follows both the toolbar drop-down and `USE` (`ISLStagingPlatformDb` → `master` after `USE master;`) | Diagnostics dialog, tester |
| Disconnected window | `CurrentlyActiveWndConnectionInfo` is **null** (no stale data), and `Query.IntelliSenseEnabled` is **unavailable** (`available=False`). A dialog first read as "stale after disconnect" was a connected window after `USE master` (withdrawn) | Diagnostics dialog |
| Events | `ScriptFactory` exposes **no public events**: connection changes must be polled or hooked elsewhere | Diagnostics dialog |
| Window object | `SqlScriptEditorControl` (public) with `Connection`, `ConnectionInfoList`, `IsConnected` | Diagnostics dialog |

Criterion 6 met: server, user, authentication kind and current database are readable through public types
without a compile-time dependency; null means "not connected". The disconnected run also confirms D1's root cause:
the IntelliSense toggle is unavailable without a connection, which the 0.5.1 retry covers. Open: the Entra `AuthenticationType` value (Phase 2, with S5).

Blast radius: all access is to public types in SSMS-private, non-redistributable assemblies, late-bound by name.
A rename or move breaks the connection feature only (the probe degrades to "not found"); editor features are
unaffected. Mitigation: one adapter class, name-based binding, a start-up self-test logged per SSMS version.

### Step 4 — Write, key bindings, uninstall (2026-11-03, SSMS 22.11, VSIX 0.7.0)

| Fact | Value | Source |
|---|---|---|
| Write | *Uppercase selection* replaced 28 characters (one line, buffer v63 → v64) and 69 characters (five lines, v110 → v111) in **one edit each**; the query then executed normally | Screenshots (status bar) |
| Undo / box selection | Undo-in-one-step and Alt+drag box selection not explicitly reported (the box-selection screenshot shows the earlier multi-line result). Box selections map to several spans and go through the same single edit | Screenshot, code |
| Default binding | `Text Editor::Ctrl+K, Ctrl+Y` applied from the `.vsct`; **no** SSMS command on the same chord or on `Ctrl+K` alone | Diagnostics dialog |
| Rebinding | User binding `Text Editor::Ctrl+Alt+U` added via Options → Keyboard and **persisted across an SSMS restart**, alongside the default | Diagnostics dialog (new engine pid) |
| Uninstall | First SSMS session after the test (19:51 UTC): **no** `SqlCompanion` / package-GUID entries in `ActivityLog.xml`; its 8 errors are all SSMS's own (Copilot brokered services, Unified Settings, C# CodeLens registration) | `ActivityLog.xml` |
| **Finding F4** | `CurrentlyActiveWndConnectionInfo` follows the **active window**, not the active document: with Object Explorer focused it returns Object Explorer's server connection (`DATABASE` null) and the IntelliSense toggle reads unknown. The adapter must resolve and cache the connection **per query document** on its activation, never at an arbitrary moment | Diagnostics dialog |

## Outcome (row B)

| # | ADR-0005 criterion | Result | Evidence |
|---|---|---|---|
| 1 | Install / upgrade / uninstall | ✅ 10+ upgrades, survives SSMS self-update, clean uninstall | Steps 1–4 |
| 2 | Text read / write, caret | ✅ events, single-edit write | Steps 1, 4 |
| 3 | Completion vs IntelliSense | ✅ Replace per window (ADR-0006); merge feasible | Steps 3–3e |
| 4 | Commands and key bindings | ✅ default chord, no clash, rebinding persists | Step 4 |
| 5 | Out-of-process worker | ✅ restart < 1 s with resync, no orphan, multi-day soak | Steps 2–3 |
| 6 | Active connection | ✅ public types, late-bound by name; per-document caching required (F4) | Step 6 |
| 7 | Completion latency | ✅ p95 ≤ 118 ms cold, 3 ms warm | Steps 3–3e |

Residual risks for Phase 3a: SSMS-private assemblies move between releases (seen: `ServiceCache` →
`SqlPackageBase.dll`); reliance on VS 2026's compatibility layer for SDK 17.x extensions; no SSMS 21 evidence.

## Decision rule

- **Clean** — steps 1–5 pass on rows A and B, and step 6 needs no non-public API: accept ADR-0005 with **SSMS first**.
- **Otherwise** — accept ADR-0005 with **standalone first**; record failing steps as Phase 4 risks for the SSMS adapter.

## Deliverables

- `spikes/S1.SsmsHost/` — the throwaway VSIX (not shipped).
- This document updated with the evidence table per row and the decision.
- ADR-0005 status changed to Accepted.
