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
| Engine service contracts | Defined (`IDiagnosticService`, `IFormattingService`, `ICompletionService`) |
| LSP server (stdio) | initialise / sync / diagnostics / completion / formatting / shutdown |
| CLI | `lex`, `analyze` |
| Formatter | Identity placeholder (Phase 1) |
| Completion | Keywords + document variables (Phase 2 makes it scope-aware) |
| Metadata | Contracts only (spike S3) |
| Spikes S1–S5 | Not started |

## Layout

```text
src/
  SqlCompanion.Engine.Abstractions   text spans, line mapping, diagnostics, service contracts
  SqlCompanion.Engine.Syntax         lexer, tokens, trivia, keyword table
  SqlCompanion.Engine.Binding        completion (binder/scope graph from Phase 2)
  SqlCompanion.Engine.Formatting     formatter (Phase 1)
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
dotnet run --project src/SqlCompanion.Cli -- lex path/to/script.sql

# Diagnostics in MSBuild canonical format; exit code 1 on errors
dotnet run --project src/SqlCompanion.Cli -- analyze path/to/*.sql

# Benchmarks
dotnet run -c Release --project bench/SqlCompanion.Benchmarks -- --filter '*'
```

The language server (`sqlwc-lsp`) speaks LSP over stdio and can be attached to any LSP client.
