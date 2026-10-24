# Spike S1 — SSMS host (throwaway)

Prototype for the [S1 protocol](../../docs/spikes/S1-ssms-host.md). **Not for production use.**

| Step | Version | What it does |
|---|---|---|
| 1 | 0.1.0 | Loads into SSMS 21/22, logs host facts and editor content types, adds **Tools → SQL Companion: S1 diagnostics** |
| 2 | 0.2.0 | Runs the engine (`sqlwc-lsp`, .NET 10) **out of process** and shows its diagnostics as squiggles in query windows; restarts it if it dies |
| 3 | 0.3.0 | Keyword and `@variable` completion from the engine (marked **SQL Companion**) alongside SSMS IntelliSense; logs which completion machinery SSMS uses; logs engine readiness, squiggle changes and latency |

Requires the **.NET 10 runtime** (the engine ships framework-dependent).

## Get the VSIX

Built on GitHub Actions: latest **Spike S1 (SSMS extension)** run → *Artifacts* → `S1.SsmsHost-vsix` → unzip.
Or locally (Visual Studio 2026 with the extension development workload):
`msbuild spikes\S1.SsmsHost\S1.SsmsHost.csproj /restore /p:Configuration=Debug`.

## Install / upgrade / uninstall

Close SSMS, double-click the `.vsix`. Installing 0.2.0 over 0.1.0 is the **upgrade** test.
Uninstall (from SSMS's `Common7\IDE`): `VSIXInstaller.exe /uninstall:SqlCompanion.S1.SsmsHost.3fc294e9-e11e-40ef-90f0-ac920191c57d`.

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
