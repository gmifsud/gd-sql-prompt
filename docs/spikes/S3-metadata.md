# Spike S3 — Metadata catalogue load and staleness probe

- Status: **Answered and confirmed on Windows** (sandbox container + developer machine, local instance). Remote
  (LAN) run still pending.
- Harness: [`spikes/S3.Metadata`](../../spikes/S3.Metadata)
- Informs: Phase 2 metadata provider (`ICatalogueProvider`), CMP-013 (refresh / invalidation), spec §13
  (10,000 objects / 100,000 columns; report cold / warm / local / remote separately)

## Questions

1. What does a full catalogue load cost at the spec's scale, and where does the time go?
2. Is `MAX(sys.objects.modify_date)` (the roadmap's assumption) a sound invalidation key? If not, what is the
   cheapest sound one?

## Method

| Part | What it does |
|---|---|
| Catalogue load | Seven queries, one round trip each, user objects only: objects, columns, parameters, FK columns, schemas, types, synonyms. **Cold** = connection pool cleared (TCP + TLS + auth) and first execution; **warm** = median / p95 of repeats; **parallel** = each query on its own pooled connection |
| Synthetic scale | Scratch database: 5,000 tables × 20 columns, a PK each, an FK on every other table, one view and one three-parameter procedure per ten tables → 13,500 objects, 110,000 columns |
| DDL truth table | Scratch database: 19 schema changes and 3 data-only controls, one per batch; after each, every candidate token is re-read. Unchanged after a schema change = **miss**; changed after a control = **false positive** |
| Probe cost | Median / p95 of 20 warm executions per candidate at synthetic scale |

Scratch databases carry a marker extended property and are dropped afterwards; a same-named database without the
marker is never touched. Reports contain counts, timings and the server version only — no object or database names.

## Candidate tokens

| Token | Definition |
|---|---|
| P1 | `MAX(modify_date)` over user objects |
| P2 | Over user objects: `COUNT_BIG`, `MAX(modify_date)`, `CHECKSUM_AGG(CHECKSUM(object_id, modify_date))`; plus count and checksum of `sys.schemas` (`schema_id`, `name`) and of user-defined `sys.types` (`user_type_id`, `name`) |
| P3 | P2 plus a checksum over column shape (`sys.columns`) |

## Results — sandbox (2026-11-04)

Environment: SQL Server 2025 **17.0.5035.5** Developer (Linux container), same host over loopback; client 4 logical
CPUs, .NET 10.0.13, Microsoft.Data.SqlClient 7.1.1, SQL authentication. Loopback is the best case for network cost.

### DDL truth table

| Change | P1 | P2 | P3 |
|---|---|---|---|
| create / rename table; add, retype, rename (`sp_rename`), drop column | ✅ | ✅ | ✅ |
| create / alter view (new column); create / alter procedure (new parameter); create synonym | ✅ | ✅ | ✅ |
| create table in new schema; transfer table between schemas | ✅ | ✅ | ✅ |
| **drop an older table** | ❌ | ✅ | ✅ |
| **drop an older view** | ❌ | ✅ | ✅ |
| **create schema** / **drop empty schema** | ❌ ❌ | ✅ ✅ | ✅ ✅ |
| **create user type** / **drop user type** | ❌ ❌ | ✅ ✅ | ✅ ✅ |
| controls: insert rows, update statistics, no-op | — | — | — |
| **Misses / false positives** | **6 / 0** | **0 / 0** | **0 / 0** |

P1 is unsound: a drop removes a row without raising the maximum, and schemas and types have no `modify_date`.
Column changes (including `sp_rename`) do update the table's `modify_date`, so P3's column checksum adds nothing.

### Load and probe cost at 13,500 objects / 110,000 columns

| Measure | Value |
|---|---|
| Cold open (TCP + TLS + auth) | 78 ms |
| Cold load, sequential | **726 ms** (columns 501 ms = 69 %; objects 91; FKs 62; parameters 56) |
| Warm load, sequential (median / p95) | **258 / 289 ms** |
| Warm load, parallel connections (median) | 214 ms (columns bound the critical path) |
| Retained in memory | 34 MB (boxed rows: upper bound for a typed snapshot) |
| Probe P1 / **P2** / P3 (median / p95) | 6.9 / 8.1 · **7.3 / 8.2** · 90.8 / 114.0 ms |
| Synthetic build | 8 s |

## Findings

| # | Finding | Consequence |
|---|---|---|
| F1 | `MAX(modify_date)` misses drops of non-latest objects and every schema and user-type change | **Adopt P2** as the staleness token; correct the roadmap and the `ICatalogueProvider` doc comment |
| F2 | P2 costs the same as P1 (≈ 7 ms) and has no misses or false positives in the truth table | Cheap enough to poll on query-window activation and on a background timer |
| F3 | Column metadata dominates load (69 %); parallelism gains little (258 → 214 ms) because columns are the critical path | Load **objects first and publish**, then columns: completion of object names becomes available after ≈ 100 ms; column lists follow. Never block typing on the load (spec §13) |
| F4 | Cold load at full scale is < 1 s on loopback | A full reload on a token change is affordable locally; incremental reload is an optimisation, not a requirement. Remote latency is the open question (developer-machine run) |
| F5 | `CHECKSUM_AGG` is an order-independent sum, so two compensating changes could in theory collide | Accepted risk; backstop with the manual refresh CMP-013 requires and a periodic full reload |
| F6 | Microsoft.Data.SqlClient throws `NotSupportedException` under invariant globalization, which `Directory.Build.props` enables repo-wide | The harness opts out. When the provider moves into `sqlwc-lsp` (Phase 2), the engine must opt out too, and its Linux build then needs ICU |
| F7 | Microsoft.Data.SqlClient 7.1.1 pulls `System.Security.Cryptography.Pkcs` 9.0.18 (GHSA-846f-f764-76mx high, GHSA-2fmv-rv2c-59x7 moderate) | Pinned to 10.0.14 centrally; NuGet audit (warnings as errors) caught it at build time |

## Results — developer machine (2026-11-05)

Environment: SQL Server 2025 **17.0.1145.4** Standard Developer (Windows, local instance), Windows authentication;
client 20 logical CPUs, .NET 10.0.14, Microsoft.Data.SqlClient 7.1.1.

**DDL truth table: identical to the sandbox** — P1 6 misses (drops of non-latest table and view, schema create/drop,
user type create/drop), P2 and P3 none; no false positives. F1/F2 confirmed on a second build and OS.

Synthetic (13,500 objects / 110,000 columns):

| Measure | Sandbox (Linux container) | Developer machine (Windows) |
|---|---|---|
| Cold open | 78 ms | 6 ms |
| Cold load, sequential | 726 ms | 1,099 ms (objects 161, **columns 726**, parameters 82, FKs 93) |
| Warm load median / p95 | 258 / 289 ms | 435 / 447 ms |
| Warm load, parallel | 214 ms | 321 ms |
| Retained | 34 MB | 34 MB |
| Probe P2 median / p95 | 7.3 / 8.2 ms | **14.4 / 15.8 ms** |
| Probe P3 median | 91 ms | 187 ms |

Real databases (20, names withheld; largest 8,454 objects / 37,734 columns):

| Measure | Range over the 20 databases |
|---|---|
| Cold load | 89–347 ms (floor ≈ 90 ms even for 2-object databases: first-execution compilation of the seven queries per database) |
| Warm load median | 8–159 ms |
| Probe P2 median / p95 | 1.3–9.9 / 1.8–12.8 ms |

| # | Finding | Consequence |
|---|---|---|
| F8 | Every measurement is inside the proposed local budgets: P2 p95 ≤ 15.8 ms (≤ 20), objects after 161 ms cold (≤ 300), full catalogue 1.1 s cold at spec scale (≤ 2 s) | Budgets adopted for Phase 2 as local targets |
| F9 | Cold cost has a per-database floor of ≈ 90 ms independent of size (query compilation), so switching database pays it once | Cache per (server, database); never reload on database switch if the P2 token is unchanged |
| F10 | Columns remain 66 % of the cold load on Windows | Confirms F3: publish objects first, columns second |

Remote attempt (2026-11-05, SQL Server 2022 16.0.1210.3, SQL authentication): cancelled after 68 databases because
the harness timed **every** database and lost the partial report on Ctrl+C. Fixed: the largest 10 by user-object
count are timed by default, and cancellation writes the report. Re-run pending.

## Proposed Phase 2 budgets

Working targets set from this evidence, not spec requirements. Local targets met on the developer machine (F8); remote pending.

| Measure | Local | Remote (LAN) |
|---|---|---|
| Staleness probe p95 | ≤ 20 ms | ≤ 50 ms |
| Objects available for completion after connect | ≤ 300 ms | ≤ 1 s |
| Full catalogue (10k / 100k) | ≤ 2 s | ≤ 5 s |

## Protocol — developer machine (Windows)

Run from the repository root.

1. **Local instance, everything** (creates and drops two scratch databases; needs `CREATE DATABASE`):
   ```powershell
   dotnet run -c Release --project spikes/S3.Metadata -- --server "(local)\SQL2025" --trust-server-certificate --all
   ```
2. **Remote server, read-only.** No `--all`: nothing is created; only catalogue views are read. Only the 10 largest
   databases are timed (`--max-databases N` to change); Ctrl+C still writes the report with what was measured.
   ```powershell
   $env:SQLCOMPANION_S3_PASSWORD = [System.Net.NetworkCredential]::new('', (Read-Host -AsSecureString 'Password')).Password
   dotnet run -c Release --project spikes/S3.Metadata -- --server "panama.isl.dom\release" --user eyeuser --trust-server-certificate --out artifacts/s3-remote
   ```
3. Send `artifacts/s3/s3-report.md` and `artifacts/s3-remote/s3-report.md`. Databases appear as `db1`, `db2`, …
