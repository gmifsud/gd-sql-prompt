# ADR-0007: The formatter verifies every result and refuses rather than risk a change of meaning

- Status: Accepted
- Date: 2026-11-04
- Evidence: [formatter design note](../formatter.md); 1,432-file corpus run (0 refusals, idempotent)

## Context

A formatter that occasionally changes what a script does is worse than none: users run formatted SQL against
production databases. T-SQL makes this easy to get wrong — `GO` is a batch separator only when alone on a line,
removing a space can merge tokens (`1 .5`), and comments, string literals and quoted identifiers can span lines.
The parser is error-tolerant (ADR-0002), so a layout engine also meets half-typed and unparsed code.

## Decision

1. **Whitespace and keyword casing only.** The formatter never inserts, removes or reorders tokens; comments keep
   their order and exact text; literals and comments keep their own line breaks.
2. **Verify or refuse.** Every result is re-parsed and compared with the input: same tokens (keywords
   case-insensitively), same comments, same statement and batch boundaries. A result that fails is discarded and
   the document is returned unchanged with the reason — in the service, the LSP and the CLI (exit code 3).
3. **Unknown means untouched.** Statements the parser holds as opaque or simple (DDL, `EXEC`, `PRINT`, …) or that
   carry a syntax diagnostic keep their original whitespace; only keyword casing is applied to them.
4. **Edits, not documents.** Hosts receive one edit per changed whitespace run or re-cased keyword, owned by the
   following token or comment. Range formatting keeps only edits owned by tokens on the selected lines.

## Consequences

- Correctness does not depend on the layout rules being bug-free: a layout bug costs a refusal, not a corrupted
  script. The corpus run is the regression signal (refusals must stay at zero).
- Verification doubles parse cost; at 5,000 lines the whole format still takes ≈ 65 ms (budget 2 s).
- DDL and other preserved statements are not reflowed until the parser models them (Phase 1 continuation).
- Re-casing non-reserved keywords (`TRY`, `NOCOUNT`) needs the comparison to accept them case-insensitively,
  which needs the keyword tables from the binder; until then they keep their case.
