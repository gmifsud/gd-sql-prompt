# Spike S1 — SSMS host (throwaway)

Prototype for the [S1 protocol](../../docs/spikes/S1-ssms-host.md). **Not for production use.**

| Step | Version | What it does |
|---|---|---|
| 1 | 0.1.0 | Loads into SSMS 21/22, logs host facts and editor content types, adds **Tools → SQL Companion: S1 diagnostics** |
| 2 | 0.2.0 | Runs the engine (`sqlwc-lsp`, .NET 10) **out of process** and shows its diagnostics as squiggles in query windows; restarts it if it dies |

Requires the **.NET 10 runtime** (the engine ships framework-dependent).

## Get the VSIX

Built on GitHub Actions: latest **Spike S1 (SSMS extension)** run → *Artifacts* → `S1.SsmsHost-vsix` → unzip.
Or locally (Visual Studio 2026 with the extension development workload):
`msbuild spikes\S1.SsmsHost\S1.SsmsHost.csproj /restore /p:Configuration=Debug`.

## Install / upgrade / uninstall

Close SSMS, double-click the `.vsix`. Installing 0.2.0 over 0.1.0 is the **upgrade** test.
Uninstall (from SSMS's `Common7\IDE`): `VSIXInstaller.exe /uninstall:SqlCompanion.S1.SsmsHost.3fc294e9-e11e-40ef-90f0-ac920191c57d`.

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
