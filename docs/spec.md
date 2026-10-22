Yes—you’re describing a SQL development productivity tool inspired by Redgate’s SQL Prompt, delivered both as an editor extension and as a standalone application.

I would specify it as a shared SQL intelligence engine with multiple front ends—not two separate products. That lets completion, formatting, analysis, refactoring, snippets, and AI behave consistently everywhere.

Redgate SQL Prompt’s documented feature families are code completion, formatting, snippets, navigation, code analysis and fixes, refactoring, SQL history, environment colouring, and optional AI assistance. Its main product is an SSMS/Visual Studio add-in; your standalone application would be additional scope. [red-gate](https://www.red-gate.com/products/sql-prompt/)

Below is a **specification** you can use as the starting point for development, estimation, and backlog generation.

# SQL Developer Productivity Platform — Product Specification

Version: 0.1 — initial functional and technical baseline  
Working product name: SQL Workbench Companion  
Reference product: Redgate SQL Prompt  
Primary database dialect: Microsoft T-SQL  
Delivery formats: editor extensions, standalone desktop application, and CLI

This is a comprehensive first-pass specification based on SQL Prompt’s documented capabilities, with proposed standalone and engineering requirements added. It is not yet a verified, command-by-command parity audit of every SQL Prompt option.

## 1. Product objective

Build a developer tool that helps users:

- Write SQL faster using schema-aware completion and reusable snippets.
- Format SQL consistently using personal and team styles.
- Detect correctness, maintainability, and potentially unsafe-query issues.
- Refactor scripts and database objects through reviewable changes.
- Explore database objects without interrupting the editing workflow.
- Recover previous queries and unsaved work.
- Generate, explain, and revise SQL with optional AI.
- Use the same capabilities inside an existing editor or a dedicated application.

### Intended users

- SQL developers and database engineers.
- Application developers working with relational databases.
- Database administrators.
- Data engineers and analysts.
- Teams maintaining SQL conventions across repositories.

### Scope boundaries

For the initial release:

- Prioritize SQL Server and Azure SQL Database.
- Treat other SQL dialects as future providers, not as automatically supported.
- Do not attempt to replace every administration feature in SSMS.
- Do not include database comparison, data generation, source-control management, or full database monitoring merely because related Redgate products provide them. Redgate lists these as separate products within its broader tooling suite. [red-gate](https://www.red-gate.com/products/sql-prompt/)

## 2. Product surfaces

| Surface | Intended role | Proposed priority |
|---|---|---|
| SSMS extension | SQL Prompt-style assistance inside the existing query editor | Initial target; integration feasibility spike first |
| Standalone desktop application | SQL editor, connections, database browsing, query execution, and results | Initial product scope |
| VS Code extension | Reuse SQL intelligence through an editor adapter | Later release |
| Visual Studio extension | Assistance within supported SQL editing contexts | Later release |
| CLI | Formatting, analysis, and CI checks | Early release |
| Shared engine/service | Common implementation used by every surface | Foundation |

For context, Redgate currently documents SSMS and Visual Studio integration, with AI functionality limited to SSMS. Your product should define its own explicit host-feature matrix rather than assuming every host supports every capability. [red-gate](https://www.red-gate.com/products/sql-prompt/)

Proposed priority labels:

- P0: required for the first useful release.
- P1: required for a credible SQL Prompt alternative.
- P2: advanced functionality or differentiation.

## 3. SQL intelligence and code completion

SQL Prompt’s baseline includes context-aware suggestions and a column picker that inserts selected columns in the order chosen. [red-gate](https://www.red-gate.com/products/sql-prompt/)

| ID | Capability | Functional requirement | Priority |
|---|---|---|---|
| CMP-001 | Keyword completion | Suggest valid keywords for the current syntactic position. | P0 |
| CMP-002 | Object completion | Suggest schemas, tables, views, procedures, functions, and other supported objects. | P0 |
| CMP-003 | Column completion | Resolve tables, aliases, CTEs, and derived tables to suggest relevant columns. | P0 |
| CMP-004 | Local symbols | Complete declared variables, parameters, CTE names, and supported temporary objects. | P0 |
| CMP-005 | Function assistance | Show function signatures, argument positions, and available descriptions. | P0 |
| CMP-006 | Procedure assistance | Suggest procedure names and generate parameter placeholders. | P1 |
| CMP-007 | Automatic aliases | Generate aliases using configurable conventions and collision handling. | P1 |
| CMP-008 | Join assistance | Suggest join conditions from foreign keys and explicitly configured relationships. | P1 |
| CMP-009 | Column picker | Select multiple columns with keyboard support and deterministic insertion order. | P1 |
| CMP-010 | Wildcard expansion | Replace `*` or `alias.*` with explicit column lists. | P1 |
| CMP-011 | Suggestion ranking | Rank by scope, syntactic relevance, and optional local usage history. | P1 |
| CMP-012 | Object information | Display type, schema, columns, parameters, and accessible descriptions. | P1 |
| CMP-013 | Metadata refresh | Support manual refresh, background refresh, and selective invalidation. | P0 |
| CMP-014 | Disconnected assistance | Continue syntax and cached-metadata assistance while clearly marking stale context. | P1 |
| CMP-015 | Configurable behaviour | Configure trigger delay, acceptance keys, filtering, and excluded objects. | P1 |

### Important behaviour

Completion must remain useful while the user is typing incomplete SQL. It must not require the whole document to parse successfully.

Permissions must be respected: failure to retrieve inaccessible metadata must not prevent assistance for accessible objects.

## 4. SQL formatting

SQL Prompt supports built-in and custom formatting styles, shared conventions, and optional actions such as wildcard expansion and object qualification alongside formatting. Those transformations should be separately configurable in your product. [documentation.red-gate](https://documentation.red-gate.com/sp/sql-code-formatting-and-styles)

| ID | Capability | Functional requirement | Priority |
|---|---|---|---|
| FMT-001 | Format document/selection | Format the full document or a selected syntactic region. | P0 |
| FMT-002 | Casing | Configure keyword, function, data-type, and identifier casing policies. | P0 |
| FMT-003 | Whitespace | Configure indentation, tabs/spaces, blank lines, and line endings. | P0 |
| FMT-004 | Clause layout | Configure layout for `SELECT`, joins, predicates, grouping, ordering, and DML. | P0 |
| FMT-005 | Expression layout | Configure `CASE`, function calls, expressions, and nested subqueries. | P1 |
| FMT-006 | Comma conventions | Support leading/trailing commas and list-wrapping policies. | P0 |
| FMT-007 | Alignment | Optional alignment of aliases, assignments, and declarations. | P1 |
| FMT-008 | Comment preservation | Preserve comments, hints, literals, and meaningful batch boundaries. | P0 |
| FMT-009 | Style profiles | Provide built-in styles and editable custom profiles. | P0 |
| FMT-010 | Repository configuration | Load version-controlled project formatting settings. | P1 |
| FMT-011 | Preview and diff | Preview formatting changes before applying them. | P1 |
| FMT-012 | Automation | Format on demand, optionally on save, and through CLI checks. | P1 |

Acceptance rules:

- Formatting the output again must produce no further changes.
- Formatting must not silently modify string literals or remove comments.
- Semantic transformations must be opt-in and distinct from whitespace formatting.
- When syntax is unsupported, preserve the affected text or report the limitation rather than guessing.

## 5. Snippets and templates

SQL Prompt snippets can insert predefined code at the cursor or wrap selected SQL; users can customize supplied snippets and create their own. [documentation.red-gate](https://documentation.red-gate.com/sp/sql-code-snippets)

| ID | Capability | Functional requirement | Priority |
|---|---|---|---|
| SNP-001 | Built-in library | Supply common query, DDL, transaction, and diagnostic templates. | P0 |
| SNP-002 | Custom snippets | Create, edit, duplicate, delete, categorize, and search snippets. | P0 |
| SNP-003 | Shortcut expansion | Expand an abbreviation into a snippet. | P0 |
| SNP-004 | Placeholders | Support editable fields, defaults, linked fields, and cursor placement. | P0 |
| SNP-005 | Surround selection | Wrap selected text in a template. | P1 |
| SNP-006 | Context variables | Insert available database, schema, object, date, and user-defined values. | P1 |
| SNP-007 | Sharing | Import/export snippets and load repository-managed libraries. | P1 |
| SNP-008 | Governance | Distinguish personal, project, and organization-managed snippets. | P2 |

## 6. Static analysis and quick fixes

SQL Prompt supports static analysis during typing or review, with rule-based fixes; its documentation also describes applying enabled analysis to AI-generated results. [documentation.red-gate](https://documentation.red-gate.com/sp/sql-code-analysis)

The following are proposed rule categories, not a claim that Redgate implements every listed check.

| ID | Capability | Functional requirement | Priority |
|---|---|---|---|
| ANA-001 | Syntax diagnostics | Identify invalid syntax with precise ranges and explanations. | P0 |
| ANA-002 | Resolution diagnostics | Identify unresolved objects or columns where metadata is sufficient. | P0 |
| ANA-003 | Maintainability rules | Detect ambiguous naming, inconsistent qualification, wildcard usage, and similar issues. | P1 |
| ANA-004 | Unused declarations | Identify unused variables and parameters. | P1 |
| ANA-005 | Query-risk rules | Flag configured patterns such as destructive DML without an effective filter. | P0 |
| ANA-006 | Performance heuristics | Flag selected non-sargable expressions, conversion risks, and other explainable patterns. | P1 |
| ANA-007 | Rule configuration | Enable/disable rules and configure severity by project. | P0 |
| ANA-008 | Suppressions | Support justified inline and project-level suppressions. | P1 |
| ANA-009 | Quick fixes | Apply a single fix, fix a selection, or fix supported issues in a document. | P1 |
| ANA-010 | Analysis panel | Show rule ID, severity, location, rationale, and available actions. | P0 |
| ANA-011 | CI output | Emit human-readable output, JSON, and a structured diagnostic interchange format. | P1 |
| ANA-012 | Existing-debt baseline | Allow teams to fail builds on new issues without blocking on all legacy findings. | P2 |

Finding unused variables and parameters is also explicitly documented as a SQL Prompt navigation capability. [documentation.red-gate](https://documentation.red-gate.com/sp8/navigating-your-code/finding-unused-variables-and-parameters)

Performance diagnostics must be labelled as heuristic unless supported by execution evidence. A static warning must not be presented as proof that one query will run faster.

## 7. Refactoring and code actions

SQL Prompt documents casing changes, semicolon insertion, wildcard expansion, name qualification, bracket changes, stored-procedure extraction, object renaming, and table splitting across its actions and refactoring documentation. [documentation.red-gate](https://documentation.red-gate.com/sp/sql-refactoring/sql-prompt-actions)

| ID | Capability | Functional requirement | Priority |
|---|---|---|---|
| REF-001 | Insert semicolons | Insert statement terminators using syntax-aware edits. | P1 |
| REF-002 | Qualify names | Add schema or alias qualification where unambiguous. | P1 |
| REF-003 | Identifier delimiters | Add delimiters or remove unnecessary ones safely. | P1 |
| REF-004 | Apply casing | Apply configured casing independently of full formatting. | P1 |
| REF-005 | Expand wildcard | Generate explicit columns using resolved metadata. | P1 |
| REF-006 | Local rename | Rename an alias, variable, parameter, or CTE within its valid scope. | P1 |
| REF-007 | Scripted-object rename | Update declarations and resolvable references within a script/workspace. | P1 |
| REF-008 | Database-aware rename | Generate a script covering the target object and discoverable dependencies. | P2 |
| REF-009 | Extract procedure | Convert selected SQL into a draft stored procedure with inferred parameters for review. | P2 |
| REF-010 | Split table | Generate a migration plan for moving/copying selected columns and updating discoverable dependencies. | P2 |
| REF-011 | Refactoring preview | Show affected objects, text changes, warnings, and generated SQL. | P0 |
| REF-012 | Editor undo | Apply supported document changes as one undoable operation. | P0 |

### Database-changing refactoring safeguards

- Generate scripts first; never execute simply because a refactoring command was selected.
- Require explicit execution approval.
- Report unresolved dynamic SQL, inaccessible dependencies, and external consumers.
- Distinguish document edits from database changes.
- Never describe a rename or table split as guaranteed safe.

## 8. Navigation and database exploration

These are proposed product requirements supporting the same write–inspect–refine workflow as SQL Prompt’s documented code-navigation and object-refactoring features. [documentation.red-gate](https://documentation.red-gate.com/sp11)

| ID | Capability | Functional requirement | Priority |
|---|---|---|---|
| NAV-001 | Go to definition | Navigate to declarations or accessible database definitions. | P1 |
| NAV-002 | Find references | Find resolvable references in the document, workspace, or available metadata. | P1 |
| NAV-003 | Document outline | List batches, procedures, functions, CTEs, and major script sections. | P1 |
| NAV-004 | Object search | Search accessible objects by name, schema, and type. | P0 |
| NAV-005 | Object explorer | Browse databases, schemas, tables, views, procedures, and functions. | P0 |
| NAV-006 | Object details | Inspect columns, keys, indexes, parameters, and definitions where permitted. | P0 |
| NAV-007 | Dependency view | Display known object dependencies and discovery limitations. | P2 |
| NAV-008 | Script generation | Generate supported `CREATE`, `ALTER`, and query templates. | P1 |

## 9. Query history, recovery, and environment awareness

SQL Prompt records query text over time, supports searching and browsing historical versions, and documents SQL History as SSMS-only. It also provides environment-based tab colouring. [documentation.red-gate](https://documentation.red-gate.com/sp/ssms-tab-management/sql-history)

| ID | Capability | Functional requirement | Priority |
|---|---|---|---|
| HIS-001 | Automatic snapshots | Save local query revisions with timestamps. | P0 |
| HIS-002 | Search history | Search text and filter by server, database, date, and document. | P0 |
| HIS-003 | Version comparison | Compare and restore previous query versions. | P1 |
| HIS-004 | Closed-tab recovery | Recover closed and unsaved documents. | P0 |
| HIS-005 | Crash recovery | Restore the latest durable workspace state after abnormal termination. | P0 |
| HIS-006 | Session restoration | Restore documents and connection associations without executing SQL. | P0 |
| HIS-007 | Retention controls | Configure storage limits, retention, exclusions, and deletion. | P0 |
| ENV-001 | Environment classification | Mark connections as development, test, staging, production, or custom. | P0 |
| ENV-002 | Visible environment cues | Show persistent colour and text labels in tabs and connection controls. | P0 |
| ENV-003 | Execution warnings | Require configurable approval for production or high-risk operations. | P1 |

History may contain sensitive SQL and embedded literals. Treat it as sensitive local data, with configurable capture and protection policies.

## 10. AI assistance

SQL Prompt documents text-to-SQL, SQL explanation, intelligent suggestions, query index analysis, and an SSMS chat assistant in preview. Its documentation also describes multi-line AI completion as a preview feature. [red-gate](https://www.red-gate.com/products/sql-prompt/)

| ID | Capability | Functional requirement | Priority |
|---|---|---|---|
| AI-001 | Text-to-SQL | Generate SQL from natural language using explicitly approved schema context. | P1 |
| AI-002 | Explain SQL | Explain selected SQL, assumptions, joins, filters, and transformations. | P1 |
| AI-003 | Modify SQL | Produce a reviewable change from a natural-language instruction. | P1 |
| AI-004 | Error assistance | Suggest corrections using query text and user-approved error details. | P1 |
| AI-005 | Query review | Suggest improvements with reasoning and stated uncertainty. | P2 |
| AI-006 | Index advice | Produce candidate index recommendations with trade-offs and verification steps. | P2 |
| AI-007 | Inline completion | Offer cancellable, multi-line suggestions without blocking editing. | P2 |
| AI-008 | Database chat | Answer schema questions using permitted metadata. | P2 |
| AI-009 | Context transparency | Show which SQL, metadata, history, and results will be included. | P0 for AI |
| AI-010 | Provider abstraction | Support configurable hosted providers and an optional local-model connector. | P1 |
| AI-011 | Output validation | Parse, format, and analyze generated SQL before presenting it for application. | P1 |
| AI-012 | Usage controls | Configure models, timeouts, budgets, logging, and organization policy. | P1 |

### AI security requirements

- AI is opt-in.
- Query results are excluded unless explicitly selected.
- Secrets and credentials are never intentionally included in prompts.
- Generated SQL never executes automatically.
- Schema descriptions and database content are treated as untrusted input, not instructions.
- Schema-aware generation is not presented as a guarantee of correctness.
- Formatting or successful parsing is not presented as proof that generated SQL is safe.

## 11. Standalone application requirements

This section is additional product scope, rather than documented SQL Prompt parity. SQL Prompt is positioned as an editor add-in. [documentation.red-gate](https://documentation.red-gate.com/sp11)

| ID | Capability | Functional requirement | Priority |
|---|---|---|---|
| APP-001 | Connection manager | Create, edit, group, test, and remove connection profiles. | P0 |
| APP-002 | Authentication | Support selected SQL Server authentication flows, with an explicit OS/support matrix. | P0 |
| APP-003 | SQL workspace | Provide tabs, split views, file editing, shortcuts, and command search. | P0 |
| APP-004 | Query execution | Execute a selection, current statement, or full script with defined batch handling. | P0 |
| APP-005 | Cancellation | Support cancellation, timeouts, and clear connection-state reporting. | P0 |
| APP-006 | Results | Display multiple result sets with virtualized rendering. | P0 |
| APP-007 | Messages | Show server messages, affected-row counts, timing, and navigable errors. | P0 |
| APP-008 | Result export | Export supported results to CSV and JSON; add other formats later. | P1 |
| APP-009 | Data limits | Configure fetch limits and warn about truncated results. | P0 |
| APP-010 | Transactions | Make transaction state visible and require explicit commit/rollback actions. | P1 |
| APP-011 | Read-only workflows | Offer application-level execution restrictions alongside database-enforced permissions. | P1 |
| APP-012 | Execution plans | Display supported estimated/actual plan artifacts with explicit collection controls. | P2 |
| APP-013 | Workspaces | Save files, connections, styles, snippets, and project settings together. | P1 |
| APP-014 | Diagnostics | Provide redacted logs and a support bundle. | P1 |

Do not make editable result grids part of the first release. They add a separate set of data-integrity, key-identification, concurrency, and confirmation requirements.

## 12. Proposed technical architecture

### Architecture principle

Keep database intelligence outside host-specific UI code.

```text
SSMS adapter       Standalone app       VS Code adapter       CLI
     \                    |                    |                /
      +------------ Host-independent API ---------------------+
                              |
                     SQL intelligence engine
                              |
       +----------------------+----------------------+
       |                      |                      |
 Parser / binder      Metadata catalogue     Formatting engine
       |                      |                      |
 Completion            Dependency model       Analysis / fixes
       |                      |                      |
       +------------- Refactoring planner -----------+
                              |
                  Optional AI orchestration
                              |
                Database provider abstraction
                              |
                 SQL Server / Azure SQL
```

### Recommended implementation direction

Given your .NET/Azure background, my proposed starting point is:

| Component | Proposed implementation |
|---|---|
| Shared engine | C#/.NET |
| Parser strategy | Evaluate Microsoft’s T-SQL parser tooling against an error-tolerant editor parser |
| Editor protocol | Language Server Protocol where practical; custom requests for product-specific operations |
| SSMS integration | Thin adapter, with a separate worker process where feasible |
| Metadata | Dedicated catalogue with connection-scoped caches |
| Local persistence | SQLite for history, workspace metadata, and local indexes |
| Configuration | Versioned JSON configuration with repository overrides |
| Standalone UI | Choose after a focused Windows-only versus cross-platform decision |
| AI | Provider-independent orchestration with explicit context policies |
| CLI | Reuse the same formatting and analysis engine |

These are design proposals, not claims that any chosen parser or SSMS API already satisfies every requirement.

### Mandatory feasibility spikes

Before committing to the architecture:

1. Validate extension installation, editor access, commands, and lifecycle against each target SSMS version.
2. Test parsing and completion against incomplete real-world scripts.
3. Validate authentication flows across the intended operating systems.
4. Confirm cancellation, large-result handling, and worker-process failure recovery.
5. Validate third-party dependency licensing and redistribution requirements.

## 13. Non-functional requirements

The following are proposed engineering targets, to be validated against a published benchmark environment.

| Area | Initial acceptance target |
|---|---|
| Cached completion | p95 response within 150 ms on the agreed metadata corpus |
| Editor responsiveness | Parsing, metadata retrieval, analysis, and AI must not block typing |
| Formatting | A representative 5,000-line script formats within 2 seconds |
| Analysis | Debounced, incremental where practical, and cancellable |
| Metadata scale | Benchmark at 10,000 objects and 100,000 columns |
| Recovery | Restore the latest durable snapshot after simulated termination |
| Isolation | A worker failure must not terminate the editor host |
| Core offline use | Formatting, snippets, and syntax-only analysis require no cloud service |
| Privacy | No SQL, schema, history, or results telemetry by default |
| Credentials | Use an appropriate secure credential mechanism; never ordinary settings files |
| Accessibility | Keyboard-complete workflows and accessible environment indicators |
| Configuration | Versioned schemas with migration and validation |
| Installation | Signed packages, upgrade handling, and documented rollback |
| Compatibility | Automated tests for every explicitly supported host/database combination |

Performance reporting must distinguish cold-cache, warm-cache, local-network, and remote-network scenarios.

## 14. Acceptance scenarios

### A. Schema-aware completion

Given:

```sql
SELECT c.
FROM dbo.Customers AS c;
```

When completion is invoked after `c.`, the tool must:

- Resolve `c` to `dbo.Customers`.
- Suggest accessible columns.
- Display available type information.
- Continue operating if another part of the document is incomplete.

### B. Join generation

Given:

```sql
SELECT
FROM dbo.Orders AS o
JOIN dbo.Customers AS c
```

When a discoverable foreign-key relationship exists, propose the relevant condition, for example:

```sql
ON c.CustomerId = o.CustomerId
```

When several relationships exist, present alternatives rather than silently selecting one.

### C. Formatting stability

Given the same document and style:

```text
format(format(document)) == format(document)
```

The formatter must preserve literals, comments, and batch semantics.

### D. Safe refactoring

For a database-aware rename, the tool must present:

- The resolved target.
- Discoverable affected objects.
- Generated SQL.
- Before/after changes.
- Unresolved dependencies and warnings.

No database change occurs until explicitly approved.

### E. AI isolation

Given an AI SQL-generation request:

- The user can inspect the selected context.
- No query results are included by default.
- The response is presented as a draft.
- Applying the draft and executing it are separate actions.

## 15. Delivery roadmap

### Phase 0 — feasibility and foundation

- Host-integration prototype.
- Parser and binding evaluation.
- Metadata-provider prototype.
- Shared engine contracts.
- Authentication and credential-storage design.
- Test corpus and benchmark definition.

Exit criterion: demonstrate one SQL document flowing through completion, formatting, diagnostics, and a host adapter.

### Phase 1 — useful developer release

- One primary editor surface.
- SQL Server/Azure SQL connection support.
- Object and column completion.
- Formatting profiles.
- Snippets.
- Basic syntax diagnostics.
- Local history and recovery.
- If the standalone app is the chosen first surface: execution and results.

Exit criterion: useful for daily editing without AI.

### Phase 2 — credible SQL Prompt alternative

- Column picker and join suggestions.
- Rich formatting controls.
- Configurable analysis and quick fixes.
- Syntax-aware code actions.
- Navigation and object details.
- Repository-managed styles/snippets.
- CLI checks.
- Second product surface.

### Phase 3 — advanced capabilities

- Database-aware rename.
- Procedure extraction and table-split planning.
- AI generation, explanation, chat, and inline completion.
- Execution-plan workflows.
- Organization policy and optional team synchronization.

Do not estimate all three phases as one MVP. Reliable SQL intelligence, host integration, and a standalone execution client are substantial independent workstreams.

## 16. What “all SQL Prompt features” should mean contractually

Before promising feature parity, create a traceability matrix with these columns:

```text
Reference feature
Reference documentation/version
Reference host
Availability: standard / preview / bundle-dependent
Our requirement ID
Our target host
Our acceptance test
Implementation status
Known limitation
```

This distinction matters because SQL Prompt’s documentation separates:

- SQL History as an SSMS capability.
- AI as optional and SSMS-specific.
- Some AI capabilities as preview features.
- Team sharing, bulk formatting, and bulk analysis as additional SQL Toolbelt Essentials capabilities. [red-gate](https://www.red-gate.com/products/sql-prompt/)

My recommendation is to make the shared engine the architectural foundation, choose one primary editing surface for the first release, and use the matrix above to turn “like SQL Prompt” into measurable parity. The standalone application should reuse that engine while keeping its connection, execution, transaction, and results requirements explicitly separate.