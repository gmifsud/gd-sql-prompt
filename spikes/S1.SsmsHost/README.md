# Spike S1 — SSMS host (throwaway)

Prototype for the [S1 protocol](../../docs/spikes/S1-ssms-host.md). **Not for production use.**

| Step | Version | What it does |
|---|---|---|
| 1 | 0.1.0 | Loads into SSMS 22 (SSMS 21 dropped by ADR-0005), logs host facts and editor content types, adds **Tools → SQL Companion: S1 diagnostics** |
| 2 | 0.2.0 | Runs the engine (`sqlwc-lsp`, .NET 10) **out of process** and shows its diagnostics as squiggles in query windows; restarts it if it dies |
| 4 | 0.7.0 | **Write** test (*Tools → SQL Companion: Uppercase selection*, one undoable edit) with a default key binding **Ctrl+K, Ctrl+Y**; diagnostics list our bindings and any SSMS command on the same chord |
| 6b | 0.6.1 | Probe also reads `IsConnected`, `Live` and the window's own ADO.NET connection (`State`, `Database`) — the candidate for the **current** database |
| 6 | 0.6.0 | *S1 diagnostics* probes the **active query window's connection** (server, database, authentication kind) and records whether each hop is public API |
| 3e | 0.5.2 | A word-ending character (space, `.`, `(` …) closes our completion session without inserting anything, so `SELECT @` opens a fresh list with `@variables`; Tab/Enter still commit |
| 3d | 0.5.1 | Windows not ready yet (start-up, not connected) are re-checked every second instead of waiting for the next activation; closed windows are forgotten |
| 3c | 0.5.0 | Completion mode applied **per query window** on activation (SSMS's toggle is per window); windows we switched off are restored individually |
| 3b | 0.4.0 | **Completion mode** (Tools → *SQL Companion: Cycle completion mode*): **Off** (SSMS only, default) → **Replace** (ours; SSMS IntelliSense switched off via its own command, restored on leaving) → **Merge experiment** (SSMS on, ours as an extra tab if SSMS's sessions call us) — [ADR-0006](../../docs/adr/0006-completion-replaces-ssms-intellisense.md) |
| 3 | 0.3.0 | Keyword and `@variable` completion from the engine (marked **SQL Companion**) alongside SSMS IntelliSense; logs which completion machinery SSMS uses; logs engine readiness, squiggle changes and latency |

Requires the **.NET 10 runtime** (the engine ships framework-dependent).

## Get the VSIX

Built on GitHub Actions: latest **Spike S1 (SSMS extension)** run → *Artifacts* → `S1.SsmsHost-vsix` → unzip.
Or locally (Visual Studio 2026 with the extension development workload):
`msbuild spikes\S1.SsmsHost\S1.SsmsHost.csproj /restore /p:Configuration=Debug`.

## Install / upgrade / uninstall

Close SSMS, double-click the `.vsix`. Installing 0.2.0 over 0.1.0 is the **upgrade** test.
Uninstall (from SSMS's `Common7\IDE`): `VSIXInstaller.exe /uninstall:SqlCompanion.S1.SsmsHost.3fc294e9-e11e-40ef-90f0-ac920191c57d`.

## Step 4 test script (≈ 10 minutes) — write, key bindings, uninstall

1. Install 0.7.0, start SSMS, connect, open **New Query**. Run *S1 diagnostics*: the *Commands and key bindings*
   section should show `Uppercase selection` bound to `Text Editor::Ctrl+K, Ctrl+Y` and list any clashes.
2. **Write**: type `select name from sys.objects`, select it, press **Ctrl+K, Ctrl+Y** → uppercased; the status
   bar says `Write OK`. Squiggles still behave (the engine saw the edit). **Ctrl+Z** once → original text back.
   Also try the menu item, a multi-line selection, and Alt+drag (box) selection.
3. **Rebind**: *Tools → Options → Environment → Keyboard*, search `Uppercase`, assign another chord (e.g.
   Ctrl+Alt+U, scope *Text Editor*). Restart SSMS → the new chord still works; diagnostics show it.
4. Run *S1 diagnostics*, send the dialog text and `s1-host.log`.
5. **Uninstall** (last): close SSMS, then from `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE`:
   `VSIXInstaller.exe /uninstall:SqlCompanion.S1.SsmsHost.3fc294e9-e11e-40ef-90f0-ac920191c57d`
   (or *Extensions → Manage Extensions → Uninstall*). Start SSMS: no error dialog, no *SQL Companion* items in
   Tools, no `sqlwc-lsp.exe` in Task Manager. Then check `%APPDATA%\Microsoft\SSMS\22.0_*\ActivityLog.xml`
   (run `ssms.exe /log` once if absent) for `SqlCompanion` errors. Reinstall afterwards to keep testing.

## Step 6b test script (optional — criterion 6 already met on 0.6.0)

Run *S1 diagnostics* after **each** step, in the **same** query window, and label each dialog with the step number.

1. Connect (Windows auth) to `(local)\SQL2025`, database **A** chosen at connect time (or the default).
2. Change to database **B** in the toolbar drop-down.
3. Execute `USE master;`.
4. *Query → Connection → Disconnect*.
5. Send the five *Active connection* sections. Entra, if available, is a bonus (`AuthenticationType` only).

## Step 6 test script (≈ 5 minutes)

The probe is read-only and never reads passwords. Server, database and user names appear **in the dialog only**;
`s1-host.log` redacts them, so the log is safe to send.

1. Install 0.6.0. Open a query window connected to `(local)\SQL2025` with **Windows** authentication and pick a
   database in the toolbar drop-down. Run *S1 diagnostics*: check the *Active connection* section matches the
   status bar (server, user, database).
2. Change the database **in the drop-down**, run diagnostics again: does `AdvancedOptions[DATABASE]` follow?
3. Run `USE master;` in the window, run diagnostics again: does it follow now?
4. **New Query** that is **not connected** (Disconnect, or cancel the connect dialog): run diagnostics.
5. If you can, a connection with **SQL authentication** or **Entra**: run diagnostics (only `AuthenticationType` matters).
6. Send the *Active connection* sections (redact names if you like) and `s1-host.log`.

## Step 3e test script (≈ 5 minutes) — also covers 3d

1. Install 0.5.2. In SSMS cycle the mode to **Replace**, then **restart SSMS** (so it starts in Replace). Connect
   and stay in the start-up query window.
2. Type `DECLARE @FF INT;`, Enter, then `SELECT @` **in one go, without pausing** → the list shows `@FF` (first)
   and no SSMS "Must declare…" error. Repeat in a **New Query** a few times — it must work every time.
3. Type `sel`, press **Tab** → `SELECT` is inserted. Type `t.` → stays `t.` (no keyword inserted).
4. Close one window, run *S1 diagnostics*, cycle to **Off**; send the dialog text and `s1-host.log`.

## Step 3d test script (≈ 5 minutes)

1. Install 0.5.1 with the mode still **Replace** (it is persisted). Start SSMS so it opens a query window at
   start-up; connect as usual. **Do not click away from the query window.**
2. Within a few seconds of connecting, type `DECLARE @t INT;` then `SELECT @` → **only** SQL Companion items and no
   SSMS "Must declare…" error. (0.5.0 left SSMS IntelliSense on in this first window until you switched windows.)
3. Toolbar **New Query** → same behaviour. Close that window.
4. Run *S1 diagnostics*: the closed window must **not** be listed as "switched off by us".
5. Cycle to **Off**, then send the dialog text and `s1-host.log`. Look for `retry after N s` and
   `closed; forgotten` lines.

## Step 3c test script (≈ 5 minutes)

1. Install 0.5.0. If any query window still has SSMS IntelliSense off from the 0.4.0 test, turn it back on via
   *Query → IntelliSense Enabled* first (0.4.0 could leave one behind).
2. Open **two** query windows. In window 1: Tools → *Cycle completion mode* until **Replace**.
3. Switch to window 2 and type `DECLARE @t2 INT;` then `SELECT @` → **only** SQL Companion items, `@t2` offered,
   and no SSMS "Must declare…" error in the Error List (SSMS IntelliSense now off there too).
4. Open a **third** query window → same behaviour without doing anything.
5. Cycle to **Merge experiment**, then click into each window → SSMS IntelliSense comes back in each (its list
   with an extra *SQL Companion* tab).
6. Cycle to **Off**. Run *S1 diagnostics*; send the dialog text and `s1-host.log`.

## Step 3b test script (≈ 10 minutes)

Run each step with a **query window active** (the mode command reads SSMS's IntelliSense toggle from it).

1. Install 0.4.0 over 0.3.0, start SSMS, open **New Query**. Mode starts at **Off**: only SSMS's list should appear.
2. Tools → **SQL Companion: Cycle completion mode** → **Replace**. The dialog says whether it switched SSMS
   IntelliSense off. Type `SEL`, then `DECLARE @Total INT;` and `SELECT @` → only SQL Companion items, `@Total` offered.
   Open a **second** New Query: is SSMS IntelliSense off there too (global) or only in the first (per window)?
3. Cycle → **Merge experiment**. SSMS IntelliSense should be back on. Type `SEL` / `SELECT @`: do you see an extra
   **SQL Companion** tab or items inside SSMS's list? **Screenshot.**
4. Cycle → **Off**. SSMS behaves exactly as before installing.
5. Run **Tools → SQL Companion: S1 diagnostics** and send the dialog text plus `s1-host.log`.

## Step 3 test script (≈ 10 minutes)

1. Install 0.3.0 over 0.2.0, start SSMS, connect, open **New Query**.
2. **Completion with SSMS IntelliSense on** (default):
   - Type `SEL` — note what appears: SSMS's list only, ours only (items with the **SQL Companion** suffix and
     keyword icon), both merged in one list, or two separate pop-ups. **Screenshot it.**
   - Type `DECLARE @Total INT;` then on a new line `SELECT @` — do you see `@Total` marked SQL Companion?
   - On an empty line press **Ctrl+Space**.
3. **Completion with SSMS IntelliSense off**: *Query → IntelliSense Enabled* (toggle off; or Ctrl+Q, Ctrl+I) and
   repeat 2. Turn it back on afterwards.
4. **Squiggles** (step 2): type `SELECT 'unterminated` → red squiggle; close the quote → it clears.
5. **Crash test** (step 2): Task Manager → *Details* → end `sqlwc-lsp.exe`, type a character → squiggles and
   completion keep working within ~2 s; SSMS never freezes.
6. Run **Tools → SQL Companion: S1 diagnostics**, then close SSMS and confirm `sqlwc-lsp.exe` is gone.

Send back the screenshots from 2–3 and `%LOCALAPPDATA%\SqlCompanion\S1\s1-host.log`. The log now records which
completion providers SSMS uses, whether the editor called ours, squiggle changes, restarts and latency — no query text.

## Step 2 test script (≈ 5 minutes)

1. Start SSMS, open **New Query**, type: `SELECT 'unterminated` → a red squiggle appears under `'unterminated`;
   hover shows `SQLC0001: Unterminated string literal. (SQL Companion)`. Close the quote → squiggle disappears.
   Also try `SELECT [Name` and `/* never closed`.
2. **Typing never blocks**: type quickly in a long script; the editor must stay responsive.
3. **Crash test**: Task Manager → *Details* → end `sqlwc-lsp.exe` while typing. SSMS must not freeze. Type one
   more character: squiggles return within ~2 s. The log shows `Engine lost` and
   `Engine ready after restart in N ms; resynchronised N document(s)`.
4. **No orphan**: close SSMS → `sqlwc-lsp.exe` is gone from Task Manager.
5. Run **Tools → SQL Companion: S1 diagnostics** before closing SSMS: it shows engine pid, restarts and
   round-trip latency (p50/p95).

## Send back

- The diagnostics dialog text, and `%LOCALAPPDATA%\SqlCompanion\S1\s1-host.log` (versions, content types,
  engine events and timings — no query text).
- Anything that failed: a screenshot plus the log.
