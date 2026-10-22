# Spike S2 — Parser recovery for completion

- Status: **Complete — target met**
- Date: 2026-10-22
- Decision informed: [ADR-0002](../adr/0002-lossless-error-tolerant-cst.md) (hybrid tolerant parser) — **confirmed**
- Harness: [`spikes/S2.ParserRecovery`](../../spikes/S2.ParserRecovery) · corpus: [`tools/fetch-corpus.sh`](../../tools/fetch-corpus.sh)

## Question

Can our own error-tolerant parser recover the **completion context** (the clause at the caret and the table sources in scope) from scripts in the incomplete states that arise while typing, with ≥ 95 % accuracy?

## Answer

Yes. On 1,422 real-world scripts (26 M characters), context is correct in **98.5–99.9 %** of sampled positions across all four editing states, statement boundaries agree with ScriptDom at **100.0 % recall / 99.9 % precision**, and the parser never crashed or lost a character. It parses **≈ 3.5× faster** than ScriptDom.

| Edit state | Samples | Context correct | Clause correct | Sources correct |
|---|---:|---:|---:|---:|
| Intact (baseline) | 21,751 | **99.9 %** | 100.0 % | 99.9 % |
| Partial word | 4,885 | **99.6 %** | 99.6 % | 99.7 % |
| Clause tail deleted | 20,861 | **99.3 %** | 99.5 % | 99.7 % |
| Statement tail deleted | 21,571 | **98.5 %** | 99.0 % | 99.2 % |

<details>
<summary>By clause</summary>

| Clause | Intact | Partial word | Clause tail | Statement tail |
|---|---:|---:|---:|---:|
| SelectList | 99.9 % (8,252) | 99.6 % (1,866) | 99.4 % (8,114) | 97.9 % (8,180) |
| TableReference | 100.0 % (4,491) | 99.3 % (1,260) | 99.2 % (4,199) | 99.4 % (4,439) |
| JoinCondition | 99.7 % (984) | 99.5 % (189) | 99.7 % (953) | 99.6 % (979) |
| Where | 100.0 % (2,861) | 99.6 % (671) | 99.8 % (2,572) | 99.9 % (2,819) |
| GroupBy | 99.2 % (129) | 100.0 % (29) | 98.4 % (125) | 98.4 % (128) |
| Having | 100.0 % (12) | 100.0 % (3) | 100.0 % (12) | 100.0 % (12) |
| OrderBy | 100.0 % (230) | 100.0 % (52) | 100.0 % (191) | 99.6 % (223) |
| SetList | 99.9 % (1,054) | 99.6 % (285) | 99.9 % (1,049) | 99.6 % (1,053) |
| InsertColumns | 100.0 % (1,408) | 100.0 % (356) | 100.0 % (1,408) | 99.3 % (1,408) |
| Values | 100.0 % (2,330) | 100.0 % (174) | 97.5 % (2,238) | 96.1 % (2,330) |

</details>

| Robustness and throughput | Value |
|---|---|
| Files / characters | 1,422 / 26,028,621 |
| Parser crashes | 0 |
| Lossy trees (`Root.ToFullString() ≠ source`) | 0 — also across ≈ 69 k edited variants |
| Statement-start recall vs ScriptDom | 100.0 % (75,229 / 75,232) |
| Statement-start precision vs ScriptDom | 99.9 % (75,229 / 75,288) |
| Parse time, all files — ours / ScriptDom | ≈ 1.5 s / ≈ 5.1 s |

## Method

### Oracle (independent ground truth)

`Microsoft.SqlServer.TransactSql.ScriptDom` (`TSql170Parser`) parses the **complete** original script. A file enters the context metrics only if ScriptDom reports zero errors (1,037 of 1,422; the rest are mostly SQLCMD-mode scripts with `:setvar` / `$(var)` and remain in the robustness checks). From ScriptDom's tree the oracle derives, for any caret:

- **Scope** — the innermost `QuerySpecification`, `UpdateSpecification`, `DeleteSpecification` or `InsertSpecification` containing the caret (MERGE positions are excluded).
- **Clause** — regions start after a clause's keyword(s) and run to the next clause keyword in the same scope; a caret in trailing same-line whitespace belongs to the clause being typed.
- **Sources** — table sources of that scope's FROM (plus DML target), as `exposedName=descriptor` (`name:schema.table`, `var:@t`, `derived`). Sources are compared only for column-completion clauses.
- **Excluded positions** — TOP, OUTPUT, hints, function arguments in FROM, `SELECT … INTO` targets, OFFSET/FOR/OPTION, derived-table column lists, set-operation ORDER BY: positions whose expected context is ambiguous or not completion-relevant.

The subject is `SyntaxTree.Parse` + `CompletionContextResolver.Resolve` on the **edited** text.

### Sampling and edit states

Candidate carets are token boundaries (end of identifiers/variables/dots; start of non-keyword tokens) that the oracle classifies. Up to 40 per file are drawn with a seeded RNG (FNV-1a of the path), files > 256 KB are not sampled. Each candidate is evaluated in four states:

| State | Edit applied at caret *p* | Models |
|---|---|---|
| Intact | none | Navigating in finished code (baseline) |
| PartialWord | identifier truncated at a random inner offset | Typing a word |
| ClauseTail | delete `[p, next clause keyword of the same scope)`, insert `\n` | Writing a new predicate / select item mid-query |
| StatementTail | delete `[p, end of innermost statement)`, insert `\n` | Writing a statement top-down |

Expected sources exclude any source that touches *p* or was deleted by the edit; target/FROM de-duplication (`UPDATE c … FROM t AS c`) is applied **after** that filter, because the resolver only sees the edited text.

### Corpus (pinned)

| Source | Commit | Files | Licence |
|---|---|---:|---|
| BrentOzarULTD/SQL-Server-First-Responder-Kit | `53c380e` | 33 | MIT |
| olahallengren/sql-server-maintenance-solution | `4161757` | 9 | MIT |
| microsoft/sql-server-samples | `dac9747` | 1,099 | MIT |
| tSQLt-org/tSQLt | `4a921d0` | 281 | Apache-2.0 |

Not committed; fetched reproducibly by `tools/fetch-corpus.sh`.

## Iteration log

Each run fixed root causes found in the mismatch log. Harness corrections are listed separately from parser changes so the evidence can be weighed.

| Run | Change | Kind | Intact | Partial | Clause tail | Stmt tail | Stmt R / P |
|---|---|---|---:|---:|---:|---:|---|
| 1 | Baseline | — | 94.4 | 93.3 | 86.8 | 94.4 | 99.7 / 98.7 |
| 2 | `((SELECT …), 0)` and VALUES rows were mistaken for subqueries; clause keywords close an unclosed call (`COUNT(` ⏎ `FROM t`) | parser | 94.9 | 93.7 | 92.9 | 95.0 | 99.7 / 99.5 |
|   | De-duplicate target/FROM after filtering by edit | harness | | | | | |
| 3 | Alias after hints: `OPENJSON(…) WITH (…) AS j` | parser | 99.0 | 98.9 | 97.0 | 95.6 | — |
| 4 | `ON` no longer suppresses a following statement (`SET NOCOUNT ON` ⏎ `SELECT`); FK actions special-cased. Caret on blank lines after a continuation token stays in scope | parser, resolver | 99.3 | 99.1 | 97.9 | 98.1 | 99.8 / 99.5 |
| 5 | `THROW` (non-reserved) recognised as a statement inside IF/WHILE | parser | — | — | — | — | 100.0 / 99.5 |
|   | View / inline-TVF bodies are not statement lists in ScriptDom: compared like with like | harness | | | | | 100.0 / 99.9 |
| 6 | `@xml.nodes()` is derived; graph `FOR PATH`; `OPENROWSET(⏎BULK`; join keywords close an unclosed call (except `OPTION (HASH JOIN)`) | parser, resolver | 99.9 | 99.6 | 99.3 | 98.2 | 100.0 / 99.9 |
|   | Clause-tail cut ends at a keyword of the **same** scope (nested-scope keywords produced unbalanced, unrealistic text); `FOR XML` exclusion starts at `FOR` (ScriptDom's fragment starts at `PATH`) | harness | | | | | |
| 7 | An unclosed rightmost group (`VALUES (` ⏎ `,0` ⏎ `▌`) keeps the caret in scope | resolver | 99.9 | 99.6 | 99.3 | 98.5 | 100.0 / 99.9 |

Run 6's clause-tail gain is partly due to the harness correction; the parser changes in the same run are covered by unit tests in `ParserTests`.

## Residual errors

| Class | Where | Notes |
|---|---|---|
| Caret on a fresh line after a complete-looking token with the statement truncated | StatementTail, SelectList/Values | Genuinely ambiguous: the next line may start a new statement. A future heuristic could use indentation. |
| Dynamic SQL in strings | SelectList | Text inside literals is opaque by design. |
| Rare grammar (graph `MATCH`, `EXTERNAL` objects, `::` scoped names, `END CONVERSATION`) | Boundaries | 59 extra / 3 missed starts in 75 k; handled as opaque statements. |

## Threats to validity

- **Shared definitions.** Oracle and resolver implement the same notion of clause regions and innermost-scope sources; the metric measures syntactic agreement, not completion quality. Correlated outer scopes are deliberately out of scope for now.
- **Synthetic edits.** The four states approximate typing; real sessions also include insertions mid-line, auto-closed brackets and quoted identifiers (excluded from PartialWord because an unterminated `[` legitimately swallows the rest of the document).
- **Corpus bias.** Library and sample code is cleaner than ad-hoc scripts; 27 % of files were not oracle-checkable (mostly SQLCMD mode).
- **Iteration on the evaluation set.** Fixes were driven by this corpus; a held-out corpus (e.g. the user's own scripts) should confirm the figures before Phase 2 exits.

## Implications

1. **ADR-0002 confirmed.** The hybrid route (own tolerant parser; ScriptDom as differential oracle) delivers completion context well above target, without adopting a full T-SQL grammar.
2. **Phase 1 can start on this CST.** Statement-boundary agreement (100.0 / 99.9 %) is sufficient for batch-aware formatting and analysis; the round-trip invariant held on every input.
3. **Keep the harness in CI as a regression gate** (manual workflow now; promote to scheduled once a held-out corpus exists).
4. **Phase 2 binder** builds on `CompletionContextResolver`: add correlated outer scopes, CTE name resolution and alias-qualified column resolution against the metadata catalogue.

## Reproduce

```bash
tools/fetch-corpus.sh
dotnet run -c Release --project spikes/S2.ParserRecovery -- tests/Corpus/external artifacts/s2
# artifacts/s2/report.md, mismatches.jsonl, statement-boundaries.log
```
