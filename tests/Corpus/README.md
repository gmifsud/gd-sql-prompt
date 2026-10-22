# Test corpus

SQL fixtures shared by unit tests and benchmarks. Files are stored byte-exact (`-text` in `.gitattributes`) so line-ending and trailing-whitespace cases are preserved.

| Folder | Purpose |
|---|---|
| `acceptance/` | Inputs for spec §14 acceptance scenarios. |
| `lexical/` | Lexer edge cases: comments, literals, operators, CRLF, batches. |
| `incomplete/` | Truncated or malformed scripts — the completion/tolerance workload for spike S2. |

Every `.sql` file must satisfy the lossless round-trip invariant. Spike S2 extends this with ≥ 500 real-world scripts truncated at random offsets.
