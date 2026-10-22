# Traceability Matrix

Turns "like SQL Prompt" into measurable parity (spec §16). One row per reference feature; add rows as features are audited against the reference documentation.

Availability values: `standard` · `preview` · `bundle-dependent`.
Status values: `not-started` · `spike` · `in-progress` · `done` · `won't-do`.

| Reference feature | Reference doc / version | Reference host | Availability | Our requirement ID | Our target host | Our acceptance test | Status | Known limitation |
|---|---|---|---|---|---|---|---|---|
| Column completion via alias | SQL Prompt docs (current) | SSMS, VS | standard | CMP-003 | All | Scenario A | not-started | — |
| Join condition suggestion | SQL Prompt docs (current) | SSMS, VS | standard | CMP-008 | All | Scenario B | not-started | — |
| Format with style | SQL Prompt docs (current) | SSMS, VS | standard | FMT-001, FMT-009 | All + CLI | Scenario C | not-started | — |
| Find unused variables and parameters | SQL Prompt 8 docs | SSMS, VS | standard | ANA-004 | All | TBD | not-started | — |
| Smart rename | SQL Prompt docs (current) | SSMS, VS | standard | REF-008 | Standalone, SSMS | Scenario D | not-started | Dynamic SQL references unresolvable |
| SQL History | SQL Prompt docs (current) | SSMS only | standard | HIS-001–003 | Standalone, SSMS | TBD | not-started | — |
| Tab colouring | SQL Prompt docs (current) | SSMS | standard | ENV-001, ENV-002 | Standalone, SSMS | TBD | not-started | — |
| Text-to-SQL | SQL Prompt docs (current) | SSMS only | preview | AI-001 | TBD | Scenario E | not-started | — |
