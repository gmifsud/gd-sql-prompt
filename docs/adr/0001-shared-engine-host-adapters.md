# ADR-0001: Shared engine with thin host adapters

- Status: Accepted
- Date: 2026-10-22

## Context

The product targets SSMS, a standalone application, VS Code, Visual Studio and a CLI (spec §2). Behaviour for completion, formatting, analysis and refactoring must be identical across surfaces.

## Decision

1. All SQL intelligence lives in host-independent .NET libraries (`SqlCompanion.Engine.*`).
2. The engine is exposed through the Language Server Protocol (`SqlCompanion.Server.Lsp`), with custom requests (`sqlCompanion/*`) for operations LSP does not model (column picker, refactoring plans).
3. Hosts are thin adapters. Hosts that cannot speak LSP (e.g. SSMS) bridge to the same server process.
4. The engine runs out of process wherever the host allows, so a worker failure cannot terminate the host (spec §13, Isolation).
5. JSON-RPC transport uses `StreamJsonRpc` with header-delimited framing.

## Consequences

- One test suite verifies behaviour for every surface.
- Every engine operation must be cancellable and version-aware, since LSP is asynchronous and concurrent.
- The CLI links the engine libraries directly; no server is needed for batch format/analyse.
