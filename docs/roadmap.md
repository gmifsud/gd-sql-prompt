# Delivery Roadmap

Supersedes §15 of [spec.md](spec.md). Requirement IDs refer to the spec.

## Rationale for re-phasing

| # | Issue in spec §15 | Impact | Remediation |
|---|---|---|---|
| 1 | Phases are bundled by surface, not by risk. | Engine risk (tolerant parsing, binding) is masked by UI work. | Phase the engine independently with measurable gates; surfaces are adapters. |
| 2 | First product surface is undecided, yet Phase 1 depends on it. | Phase 1 cannot be scoped. | Decide at the Phase 0 gate on spike evidence ([ADR-0005](adr/0005-first-product-surface.md)). |
| 3 | Parser strategy is "evaluate" but is load-bearing. | Formatter, completion, refactoring and AI validation all depend on tree properties. | Fix the tree contract now ([ADR-0002](adr/0002-lossless-error-tolerant-cst.md)); only the implementation route is spiked. |

## Core invariant

A **lossless, error-tolerant concrete syntax tree**: every source character belongs to exactly one token or trivia element, and parsing never fails — unsupported syntax degrades to opaque nodes that preserve text verbatim.

## Phases

### Phase 0 — Spikes and foundations (time-box ≈ 4 weeks, single developer)

| Spike | Question | Evidence required |
|---|---|---|
| S1 SSMS host | Can a VSIX on SSMS 21+ (VS 2022 shell, x64) reliably access text buffer, completion session, commands and lifecycle, talking to an out-of-process worker? | Prototype on each target SSMS version. Extensions are not officially supported in SSMS — treat API stability as a managed risk. |
| S2 Parser | What fraction of cursor positions in truncated real scripts yield a correct completion context? | Corpus ≥ 500 scripts truncated at random offsets; target ≥ 95 % correct clause/scope recovery. |
| S3 Metadata | Cold catalogue load at 10k objects / 100k columns; viability of a cheap staleness probe. | Timings for `sys.objects`/`sys.columns`/FK queries; `MAX(modify_date)` as invalidation key. |
| S4 Standalone UI | Avalonia + AvaloniaEdit vs WPF vs Monaco-in-WebView. | One editor tab bound to the engine; keystroke→completion latency; accessibility. |
| S5 Auth / credentials | Entra ID interactive, SQL auth, integrated auth across target OSes. | `Microsoft.Data.SqlClient` prototypes; DPAPI / Keychain / libsecret storage. |

Status (2026-10-22):

| Spike | Status | Outcome |
|---|---|---|
| S1 SSMS host | Protocol ready — needs Windows + SSMS | [S1 protocol](spikes/S1-ssms-host.md) |
| S2 Parser | **Complete — target met** | 98.5–99.9 % context recovery; ADR-0002 confirmed. [S2 report](spikes/S2-parser-recovery.md) |
| S3 Metadata | Not started | Contracts in `SqlCompanion.Engine.Metadata` |
| S4 Standalone UI | Not started | — |
| S5 Auth / credentials | Not started | — |

Cross-cutting from day one: [traceability matrix](traceability-matrix.md), test corpus (`tests/Corpus`), benchmark harness (`bench/`), versioned config schema, telemetry off by default.

**Exit gate:** one document flows lexer → CST → binder → {completion, format, diagnostics}, exercised via the LSP server and the CLI; ADR-0005 accepted.

### Phase 1 — Engine core + CLI (no database, no host)

Scope: FMT-001–004, 006, 008, 009 · ANA-001, 005, 007, 010 · ANA-011 (SARIF 2.1.0) · `sqlwc format --check`, `sqlwc analyze`.

**Exit gate:**
- `format ∘ format = format` (idempotence) over the corpus, as a property test.
- `tokens≠trivia(format(x)) ≡ tokens≠trivia(x)` modulo casing policy (semantic preservation).
- 5,000-line script formats in < 2 s.
- Zero statement-boundary divergence from the ScriptDom oracle on complete scripts.

### Phase 2 — Metadata, binder, completion over LSP

Scope: provider abstraction + SQL Server/Azure SQL provider · connection-scoped catalogue with SQLite persistence · scope-graph binder · CMP-001–005, 013 · ANA-002 · SNP-001–004 · VS Code thin LSP client (development harness).

Timing rules:
- Every request carries the document version; stale-version results are discarded.
- `CancellationToken` chains from keystroke debounce through metadata fetch.
- Catalogue snapshots are immutable and swapped atomically.

**Exit gate:** acceptance scenario A at warm-cache p95 < 150 ms on the benchmark corpus; cold / local / remote reported separately.

### Phase 3 — First product surface

Branch selected by ADR-0005:

- **3a SSMS adapter** — thin VSIX + engine worker; HIS-001/002/004 and ENV-001/002 within SSMS.
- **3b Standalone** — APP-001–007, 009 · HIS-001, 002, 004–007 · ENV-001/002. SQLite (WAL) history encrypted at rest; recovery via periodic durable snapshots; session restore never executes SQL.

**Exit gate:** two weeks of daily use as a primary SQL editor, with defects logged.

### Phase 4 — Credible SQL Prompt alternative

CMP P1 · FMT P1 + repository configuration · ANA P1 + quick fixes + suppressions · REF-001–007 with REF-011/012 · NAV P1 · second surface.

### Phase 5 — Advanced

REF-008–010 · AI-* · APP-012 · organisation policy. AI is deliberately last: AI-011 (validate generated SQL) reuses parser, binder and rules; without a mature engine, §10 security requirements are unenforceable.

## Critical path

```text
Lexer/CST ──► Formatter ──► CLI ─────────────────────────────┐
    │                                                        │
    └──► Binder ◄── Metadata catalogue ◄── Provider/Auth     ├──► Phase 3 surface ──► Phase 4 ──► AI (P5)
              │                                              │
              └──► Completion / ANA-002 ──► LSP server ──────┘
                       ▲
     Dependency graph (sys.sql_expression_dependencies) ──► REF-008/010 (P5)
```

S1/S4 run in parallel with the lexer/CST → binder chain.

## Formal underpinnings

| Concern | Model | Consequence |
|---|---|---|
| Formatting | Wadler, *A Prettier Printer* — `Doc` algebra; formatter as an idempotent projection | Style profile → `Doc`; idempotence is a property test. |
| Name resolution | Scope graphs — Néron, Tolmach, Visser, Wachsmuth, *A Theory of Name Resolution* (ESOP 2015) | Logical processing order and correlated subqueries become explicit visibility edges. |
| Unused declarations | Liveness as a monotone dataflow problem on a powerset lattice; Kildall worklist over a CFG | Sound may-analysis; termination bounded by lattice height × CFG size. |
| Rename / split impact | Transitive closure on the dependency graph | Under-approximation (dynamic SQL, external consumers) — never "guaranteed safe". |
| Join suggestion | FK graph paths | Multiple candidates are surfaced, not chosen; multi-hop is k-shortest-paths. |

## Decisions

| Decision | Status | Record |
|---|---|---|
| Shared engine, thin host adapters, LSP as the host-independent API | Accepted | [ADR-0001](adr/0001-shared-engine-host-adapters.md) |
| Lossless error-tolerant CST; hybrid parser with ScriptDom oracle | Accepted | [ADR-0002](adr/0002-lossless-error-tolerant-cst.md) |
| Engine-first phasing | Accepted | [ADR-0003](adr/0003-engine-first-phasing.md) |
| Cross-platform engine/CLI, Windows-first UI | Accepted | [ADR-0004](adr/0004-platform-targets.md) |
| First product surface (standalone unless S1 is clean) | Proposed — decided at Phase 0 gate | [ADR-0005](adr/0005-first-product-surface.md) |
| Distribution model (personal/OSS vs commercial) | Open | — |

## Assumptions

- Single developer or very small team; phases largely sequential.
- .NET 10 (LTS) for engine, server and CLI.
- T-SQL only through Phase 4; provider abstraction exists as an interface but is not exercised.
