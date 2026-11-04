# Formatter (Phase 1)

Project: [`src/SqlCompanion.Engine.Formatting`](../src/SqlCompanion.Engine.Formatting). Safety contract:
[ADR-0007](adr/0007-formatter-verify-or-refuse.md).

## Pipeline

```text
text ──► SyntaxTree.Parse ──► LayoutBuilder ──► Doc ──► DocPrinter ──► formatted
                                                                          │
           original tree ◄── Verify (tokens, comments, structure) ◄── re-parse
                │
                ├─ pass: result (or minimal edits via anchors)
                └─ fail: original text + reason
```

| Component | Role |
|---|---|
| `Doc`, `DocPrinter` | Wadler/Leijen document algebra: `Text`, `Space`, `Line`, `SoftLine`, `HardLine`, `BlankLine`, `Nest`, `Group`. A group prints flat if it fits in the remaining width, else breaks. Indentation is written lazily, so no trailing whitespace and no space at a line start. O(n·w) |
| `LayoutBuilder` | CST → `Doc`. Emits every token once, in order, with its comments; chooses separators |
| `SqlFormatter` | Layout, line endings, final newline; verification; edit computation |
| `SqlFormattingService` | `IFormattingService` for the LSP (document and range; editor tab settings override the profile) |
| `StyleProfile` | Options and built-in profiles |

## Layout rules (default profile)

| Construct | Rule |
|---|---|
| Statements | One per line; one blank line kept where the source had one or more |
| Query clauses | `SELECT`, `FROM`, `WHERE`, `GROUP BY`, `HAVING`, `ORDER BY`, set operators: each starts a line |
| Lists (select list, `GROUP BY`, `SET`, `DECLARE`, `VALUES`, parenthesised lists) | Flat if they fit, else one item per line, indented |
| Predicates (`WHERE`, `HAVING`, `ON`, `IF`, `WHILE`, `CASE WHEN`) | Flat if they fit, else break before top-level `AND` / `OR` (not the `AND` of `BETWEEN`) |
| Joins | One per line, indented under `FROM`; `ON` stays on the join line |
| Subqueries, CTE bodies | Inside `(` … `)` on their own lines, indented |
| `CASE` | Flat if it fits, else one branch per line |
| Control flow | `BEGIN` aligned with `IF` / `WHILE`; bodies indented; `ELSE IF` on one line |
| Modules | Header (`CREATE PROCEDURE … AS`) preserved; body formatted |
| Preserved | Opaque, simple (DDL, `EXEC`, `PRINT`, `RETURN`, `THROW`), and statements with syntax diagnostics: original whitespace, keywords cased |
| Spacing | One space after `,` and around comparison / assignment operators; none before `, ; ) .`, after `( .`, around `::`; keyword functions (`CONVERT(`, `COALESCE(`) unspaced; elsewhere a space only where the source had whitespace (keeps `-1` unary and `b+1` as written) |
| Comments | Own-line comments stay on their own line; trailing comments stay on their line; a `--` comment before a comma moves the comma to the next line |

## Profiles

| Name | Difference from `default` |
|---|---|
| `default` | Upper-case keywords, 4 spaces, width 120, trailing commas, every clause on its own line |
| `compact` | Queries that fit stay on one line |
| `leading-commas` | `, item` at line start when a list breaks |
| `lowercase` | Lower-case keywords |

Options: `Keywords` (upper / lower / preserve), `IndentSize`, `UseTabs`, `MaxLineWidth`, `Commas`,
`CollapseShortQueries`, `IndentJoins`, `LineEndings` (preserve / CRLF / LF), `MaxBlankLines` (0–1).

## Evidence (2026-11-04)

| Check | Result |
|---|---|
| Corpus: 1,432 scripts (public corpus + fixtures) | 1,251 reformatted, **0 refused** by verification |
| Idempotence over the corpus (second pass) | **0** files change |
| Unit tests | Golden layouts, every corpus fixture × every profile (safe + idempotent), 3,000 random token sequences, verification rejects corrupted layouts, edits reproduce the formatted text, range edits stay on selected lines |
| Performance (BenchmarkDotNet, including verification) | 500 lines 3.4 ms · **5,000 lines 65 ms** (gate: < 2 s) · 45,000-line install script 1.8 s end to end via the CLI |
| Lines over 120 columns in the corpus | 64,165 → 14,775 (remainder: long literals, comments and preserved statements) |

## Known gaps

| Gap | Requirement | Note |
|---|---|---|
| Non-reserved keywords, functions, data types and identifiers are not cased | FMT-002 | Needs keyword tables from the binder and a matching verification rule (ADR-0007) |
| DDL (`CREATE TABLE`, `ALTER`), `EXEC` arguments and module headers are preserved, not formatted | FMT-004 | Needs parser structure for them |
| Expression layout options (`CASE` style, function arguments, nested subquery style) | FMT-005 (P1) | Groups exist; options do not |
| Alignment of aliases and assignments | FMT-007 (P1) | — |
| Repository configuration file | FMT-010 (P1) | Profiles are built in; no JSON profile loading yet |
