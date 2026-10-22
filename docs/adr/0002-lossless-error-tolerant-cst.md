# ADR-0002: Lossless, error-tolerant concrete syntax tree

- Status: Accepted
- Date: 2026-10-22

## Context

- The formatter must preserve comments, literals and batch boundaries, and be idempotent (FMT-008, scenario C).
- Completion must work on incomplete SQL (spec §3).
- Refactorings must produce minimal, undoable text edits (REF-012).

`Microsoft.SqlServer.TransactSql.ScriptDom` (MIT) is a complete T-SQL parser, but it is not error-tolerant: a syntax error yields errors rather than a usable tree, and regenerating text through `SqlScriptGenerator` discards comments.

## Decision

1. **Tree contract.** The syntax tree is lossless: every character of the source belongs to exactly one token or one trivia element (whitespace, newline, comment). Concatenating all tokens with their trivia reproduces the source byte-for-byte.
2. **Tolerance.** Parsing never throws. Missing tokens are synthesised as zero-width tokens with a diagnostic; unrecognised input is captured as skipped-token trivia; unsupported statements become `OpaqueStatement` nodes that preserve text verbatim.
3. **Implementation route (hybrid).**
   - Own lexer (lossless).
   - Own recursive-descent parser for the high-traffic subset: DML, CTEs, `DECLARE`/`SET`, control flow, common DDL.
   - ScriptDom acts as a differential-testing oracle on complete scripts (statement boundaries, validity) and parses module definitions from `sys.sql_modules` where full fidelity matters.
4. Spike S2 measures completion-context recovery on truncated scripts; the parser subset is extended by corpus evidence, not speculation.

## Evidence (spike S2, 2026-10-22)

[S2 report](../spikes/S2-parser-recovery.md): on 1,422 real-world scripts the tolerant parser recovered the completion context in 98.5–99.9 % of sampled carets across four editing states (target ≥ 95 %), agreed with ScriptDom on statement starts at 100.0 % recall / 99.9 % precision, produced zero crashes and zero lossy trees, and parsed ≈ 3.5× faster than ScriptDom. The implementation route is confirmed.

## Consequences

- Higher up-front cost than ScriptDom alone; mitigated by restricting the owned grammar to what completion and formatting need.
- The round-trip invariant is testable from the first commit (lexer) and remains a regression guard for every later layer.

## Alternatives considered

| Option | Rejected because |
|---|---|
| ScriptDom only | Not error-tolerant; comment loss on regeneration. |
| Tree-sitter T-SQL grammar | Weak T-SQL coverage; native interop from .NET; grammar ownership outside our control. |
| Full hand-written T-SQL grammar | Grammar surface is very large; unnecessary when opaque statements preserve unsupported syntax. |
