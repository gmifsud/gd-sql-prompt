# Spike S1 — SSMS host (throwaway)

Step 1 of the [S1 protocol](../../docs/spikes/S1-ssms-host.md): load into SSMS 21/22, record host facts and
editor content types, prove caret/edit events and a menu command. **Not for production use.**

## Get the VSIX

Built on GitHub Actions (no local Visual Studio extension workload needed): open the latest
**Spike S1 (SSMS extension)** run → *Artifacts* → `S1.SsmsHost-vsix` → unzip → `SqlCompanion.S1.SsmsHost.vsix`.

## Install / uninstall

1. Close SSMS.
2. Double-click the `.vsix` (VSIXInstaller should offer SSMS 22). If it does not find SSMS, record that —
   it is S1 evidence — then try: `"<SSMS install>\Common7\IDE\VSIXInstaller.exe" SqlCompanion.S1.SsmsHost.vsix`.
3. Start SSMS, connect, open a **New Query** window, type a few characters, move the caret.
4. Run **Tools → SQL Companion: S1 diagnostics**.

Uninstall: `"<SSMS install>\Common7\IDE\VSIXInstaller.exe" /uninstall:SqlCompanion.S1.SsmsHost.3fc294e9-e11e-40ef-90f0-ac920191c57d`.

## Send back

- The diagnostics dialog text (or the **SQL Companion (S1)** Output pane).
- `%LOCALAPPDATA%\SqlCompanion\S1\s1-host.log`.
- If anything failed: the installer message and SSMS's `ActivityLog.xml` (run `Ssms.exe /log` once to produce it).

The log contains host versions and editor content types only — no query text.
