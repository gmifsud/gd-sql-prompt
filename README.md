# SQL Workbench Companion

A T-SQL developer productivity platform — schema-aware completion, formatting, analysis, refactoring and snippets — built as one shared engine behind multiple surfaces (LSP editors, SSMS, standalone app, CLI).

- Specification: [docs/spec.md](docs/spec.md)
- Roadmap and phase gates: [docs/roadmap.md](docs/roadmap.md)
- Architecture decisions: [docs/adr/](docs/adr)
- Parity tracking: [docs/traceability-matrix.md](docs/traceability-matrix.md)

## Status: Phase 0 (spikes and foundations)

| Area | State |
|---|---|
| Lossless, error-tolerant lexer | Implemented; round-trip invariant property-tested |
| Tolerant parser (CST) | Implemented for the high-traffic subset; spike S2 passed ([report](docs/spikes/S2-parser-recovery.md)) |
| Completion context resolver | Clause + in-scope sources at a caret (seed of the Phase 2 binder) |
| Engine service contracts | Defined (`IDiagnosticService`, `IFormattingService`, `ICompletionService`) |
| LSP server (stdio) | initialise / sync / diagnostics / completion / formatting / shutdown |
| CLI | `lex`, `analyze`, `format` |
| Formatter | v0.1: clause layout, lists, predicates, CASE, control flow; 4 profiles; verify-or-refuse ([design](docs/formatter.md)) |
| Completion | Keywords + document variables (Phase 2 makes it scope-aware) |
| Metadata | Contracts only (spike S3) |
| Spike S1 (SSMS) | **Complete** — clean on SSMS 22; first surface is the SSMS adapter, SSMS 22+ ([evidence](docs/spikes/S1-ssms-host.md), [ADR-0005](docs/adr/0005-first-product-surface.md)) |
| Spike S2 (parser) | Complete — target met |
| Spike S3 (metadata) | Question answered: composite staleness token, full load < 1 s at spec scale ([report](docs/spikes/S3-metadata.md)); Windows run pending |
| Spike S4 (standalone UI) | Deferred to Phase 4 (ADR-0005) |
| Spike S5 (auth) | Not started |

## Layout

```text
src/
  SqlCompanion.Engine.Abstractions   text spans, line mapping, diagnostics, service contracts
  SqlCompanion.Engine.Syntax         lexer, tolerant parser, lossless CST
  SqlCompanion.Engine.Binding        completion context resolver, completion
  SqlCompanion.Engine.Formatting     formatter: Doc algebra, layout, verify-or-refuse
  SqlCompanion.Engine.Analysis       diagnostics and rules
  SqlCompanion.Engine.Metadata       catalogue snapshot and provider contracts
  SqlCompanion.Server.Lsp            sqlwc-lsp — Language Server over stdio
  SqlCompanion.Cli                   sqlwc — command-line tools
tests/
  Corpus/                            byte-exact SQL fixtures shared by tests and benchmarks
  SqlCompanion.Engine.Tests
  SqlCompanion.Server.Lsp.Tests      end-to-end LSP over in-memory streams
bench/
  SqlCompanion.Benchmarks            BenchmarkDotNet
spikes/
  S2.ParserRecovery                  parser-recovery harness (ScriptDom oracle)
  S3.Metadata                        catalogue load and staleness-probe harness (SQL Server)
  S1.SsmsHost                        SSMS 22 extension prototype (VSIX, built on Windows CI)
tools/
  fetch-corpus.sh                    pinned external T-SQL corpus for spikes
```

## Build and test

Requires the .NET 10 SDK.

```bash
dotnet build -c Release
dotnet test -c Release
```

Tests run on Microsoft.Testing.Platform (opted in via `global.json`).

## Tools

```bash
# Token stream with trivia
dotnet run --project src/SqlCompanion.Cli -- lex tests/Corpus/lexical/literals.sql

# Diagnostics in MSBuild canonical format. Accepts files, directories (recursive *.sql)
# and glob patterns; wildcards are expanded by sqlwc itself, so they work in cmd/PowerShell too.
dotnet run --project src/SqlCompanion.Cli -- analyze tests/Corpus/incomplete/*.sql
dotnet run --project src/SqlCompanion.Cli -- analyze "D:\scripts\**\*.sql"
dotnet run --project src/SqlCompanion.Cli -- analyze D:\scripts

# Format: print to stdout, or check / rewrite in place (encoding and BOM preserved)
dotnet run --project src/SqlCompanion.Cli -- format query.sql
dotnet run --project src/SqlCompanion.Cli -- format --check "D:\scripts\**\*.sql"
dotnet run --project src/SqlCompanion.Cli -- format --write --style leading-commas D:\scripts

# Benchmarks
dotnet run -c Release --project bench/SqlCompanion.Benchmarks -- --filter '*'

# Spike S2 harness (fetches ~1,400 public scripts at pinned commits)
tools/fetch-corpus.sh
dotnet run -c Release --project spikes/S2.ParserRecovery -- tests/Corpus/external artifacts/s2

# Your own scripts; --placeholders lets ScriptDom check templated SQL ({name}, $(name), <name, type, default>)
dotnet run -c Release --project spikes/S2.ParserRecovery -- D:\my-scripts artifacts/s2-local --placeholders

# Spike S3 harness (SQL Server). --all creates and drops scratch databases: local instances only
dotnet run -c Release --project spikes/S3.Metadata -- --server "(local)\SQL2025" --trust-server-certificate --all
```

`sqlwc` exit codes: `0` success · `1` SQL errors reported (`analyze`) or files need formatting (`format --check`) · `2` invalid input (a path or pattern matched no file, or a file could not be read) · `3` the formatter's safety check left a file unchanged (a formatter bug; please report it).

The language server (`sqlwc-lsp`) speaks LSP over stdio and can be attached to any LSP client.
