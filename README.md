# ColumnstoreAnalyzer (C#)

:::note This CLI has been mostly written by Claude, with lots of prompts and data and research by me (itssimple)

A .NET 10 console tool you point at any SQL Server database — no schema knowledge needed.
It discovers the large tables itself, then does what the DMV-only approach can't:
**it reads the actual data** and measures how repetitive each column is, because
columnstore compression (dictionary + run-length encoding) lives and dies on duplicate
values. Tables full of duplicated data are exactly where columnstore can replace stacks
of wide nonclustered B-tree indexes.

The scoring model follows the columnstore guidance popularized by Brent Ozar and the
Microsoft docs:

1. **Repetitive data compresses; unique data doesn't.** Low-cardinality columns
   (status codes, foreign keys, dates, flags, category names) are columnstore gold.
2. **Size matters.** A rowgroup holds up to 1,048,576 rows — a table needs many millions
   of rows before columnstore even forms enough full rowgroups to pay off. Small tables
   are flagged as "too small to benefit" regardless of how pretty the data looks.
3. **Scans good, seeks fine, UPDATEs bad.** Analytic scan-heavy access is rewarded;
   UPDATE-dominated write mixes are penalized hard (updates = delete-bitmap + delta-store
   churn = fragmentation).
4. **High-cardinality wide strings are poison.** Near-unique varchar/nvarchar columns
   (GUID strings, URLs, comments) cause dictionary pressure, which trims rowgroups and
   ruins compression. The tool flags these per column.

## What it does, step by step

1. Discovers user tables meeting size thresholds (default: ≥1M rows OR ≥500 MB), top N by size
2. Skips data sampling for tables that already have a columnstore index
3. For each candidate, per column: `COUNT_BIG(DISTINCT)`, null count, avg byte length —
   over the full table when small, over a `TABLESAMPLE` (~1M rows by default) when large,
   batched ~20 columns per query with `MAXDOP 2` and read-uncommitted so it's gentle on prod
4. Pulls the workload picture from DMVs: seek/scan/update mix, lock & IO-latch waits,
   insert/update/delete leaf counts, missing-index suggestion counts, NC index footprint
5. Inventories every index (size, key + included columns) and flags **wide** indexes
   (5+ includes) and **written-but-never-read** drop candidates
6. Scrapes the plan cache once and matches cached statements to each table by name —
   your proof the tables are actually used, ranked by logical reads
7. Supplements that with Query Store's persisted, restart-surviving query history for the same
   database (if Query Store is on) — same name-matching approach, merged into the same evidence,
   so a plan-cache eviction or instance restart doesn't erase your workload proof. If Query Store
   is off, or stuck `READ_ONLY` because it hit its storage cap (a real, silent-failure gotcha),
   you'll see a one-line note and the plan-cache evidence stands alone, same as before this existed
8. Scores every table 0–100 and writes reports

## Build & run

```bash
dotnet build -c Release
# Windows integrated auth:
dotnet run -c Release -- --server SQLPROD01 --database SalesDW
# SQL auth:
dotnet run -c Release -- --server SQLPROD01\INST1 --database SalesDW --user analyst --password '...'
```

Or publish a self-contained exe: `dotnet publish -c Release -r win-x64 --self-contained`

Permissions: `VIEW SERVER STATE` + read access to the database.

### Options

**Connection / auth**

| Flag | Default | Meaning |
|------|---------|---------|
| `--server`, `-s` | *(required)* | Server\instance name |
| `--database`, `-d` | *(required)* | Target database |
| `--user`, `-u` | Windows integrated auth | SQL auth login - prompts for a masked password interactively |
| `--password`, `-p` | — | SQL auth password. Discouraged: ends up in shell history / process lists |
| `--password-stdin` | off | Read the SQL auth password from stdin, for scripts (`echo $PW \| tool ...`) |
| `--no-trust-cert` | off | Disable `TrustServerCertificate=true` |

**Table discovery & sampling**

| Flag | Default | Meaning |
|------|---------|---------|
| `--min-rows` | 1,000,000 | Table qualifies at this row count... |
| `--min-mb` | 500 | ...or at this size (either passes) |
| `--top` | 25 | Max tables fully analyzed |
| `--sample-rows` | 1,000,000 | Target sample size for cardinality measurement |
| `--column-batch` | 20 | Columns per cardinality query |
| `--timeout` | 600 | Per-query timeout (seconds) |
| `--max-queries` | 20 | Cached queries kept per table |
| `--output`, `-o` | timestamped folder | Report destination |
| `--no-html-report` | off (report is written) | Skip writing the self-contained `report.html` dashboard |

**Optional local-LLM narrative** (second opinion alongside the built-in rule-based one)

| Flag | Default | Meaning |
|------|---------|---------|
| `--llm-endpoint` | — | OpenAI-compatible endpoint, e.g. `http://localhost:11434` (Ollama), `http://localhost:1234` (LM Studio). Omit to skip the LLM step entirely |
| `--llm-model` | `llama3.1` | Model name at the endpoint |
| `--llm-key` | — | Bearer token, if the endpoint needs one |
| `--llm-timeout` | 300 | LLM request timeout (seconds) |

**Health check** (opt-in; see "Health check (optional, new)" below for the full permission/safety writeup)

| Flag | Default | Meaning |
|------|---------|---------|
| `--health-check` | off | Run the whole opt-in health-check stage alongside columnstore analysis |
| `--skip-frk` | off | Don't detect/run the sp_Blitz family |
| `--skip-ola` | off | Don't detect Ola Hallengren maintenance jobs |
| `--skip-whoisactive` | off | Don't detect sp_WhoIsActive |
| `--include-blitzlock` | off | Also run sp_BlitzLock if installed (deadlock analysis) |
| `--include-blitzbackups` | off | Also run sp_BlitzBackups if installed (overlaps the native backup-recency check) |
| `--tools-database` | `master` | Database FRK / sp_WhoIsActive are installed in |
| `--blitzfirst-seconds` | 30 | Extra live `@Seconds=` sample captured alongside the always-run instant sp_BlitzFirst snapshot; adds that many seconds of real wall-clock time. `0` skips the extra sample |
| `--health-check-all-databases` | off (connected DB only) | Widen backup-recency/CHECKDB-age/config checks and finding filtering to every database on the instance |
| `--install-missing-tools` | off | **Opt-in escalation.** If FRK/Ola Hallengren are missing, offer to download (pinned version, SHA-256 verified), confirm interactively, and install them. Grants this run `CREATE PROCEDURE` (usually in `master`) and SQL Agent job creation rights - never on by default, aborts safely if run non-interactively |

**Permissions inventory** (standalone - see "Permissions inventory (optional, new)" below)

| Flag | Default | Meaning |
|------|---------|---------|
| `--permissions-report` | off | Runs a logins/permissions security checkup across **every** database on the instance, instead of the columnstore analysis (not alongside it). `--database` is not required and is ignored when this is set |

**Self-test** (standalone - see "Self-test (optional, new)" below)

| Flag | Default | Meaning |
|------|---------|---------|
| `--self-test` | off | Strictly read-only diagnostic pass exercising every code path in the tool, instead of any other mode. `--database` is not required. Cannot be combined with `--install-missing-tools` |

## Output

Console shows a ranked summary; the output folder gets:

- `1_ranked_candidates.csv` — one row per table: score, data-shape metrics
  (% low-cardinality columns, median duplication factor, dictionary-pressure column count),
  workload metrics (scan %, write %, lock waits), NC index bloat ratio, notes
- `2_column_analysis.csv` — **the duplicate-data report**: per column, distinct ratio,
  duplication factor (avg repeats per value), null count, avg byte length, and a verdict
  (EXCELLENT / GOOD / NEUTRAL / POOR / BAD-dictionary-pressure)
- `3_index_inventory.csv` — every index with size and columns; wide and dead indexes flagged
- `4_referencing_queries.csv` — statements per table, ranked by logical reads, from the plan
  cache and (when available) Query Store — a `source` column tells you which
- `5_analysis.md` — narrative analysis of each table, with punch-list of reclaimable MB
- `6_llm_analysis.md` — optional second-opinion narrative from a local LLM (if endpoint provided)
- `full_report.json` — everything, machine-readable
- `7_health_check_findings.csv` / `health_check.json` — only written with `--health-check` (see below)
- `report.html` — self-contained dashboard (columnstore + health-check findings together); written by
  default, `--no-html-report` to skip

## Reading the results

- **Score ≥ 60**: strong candidate. Typical profile: most columns ≤10% distinct, scan-heavy,
  NC indexes as big as (or bigger than) the data. Consider a clustered columnstore and
  retiring the wide NC indexes (keep a B-tree PK on top for uniqueness if needed — supported
  since 2016).
- **40–60**: worth testing — often "keep the rowstore clustered index, add a *nonclustered*
  columnstore for the analytic queries" territory, especially if write % is meaningful.
- **< 40**: leave alone. Usually too small, too unique, or too UPDATE-heavy.

Cross-check the score against `2_column_analysis.csv`: if the big wide NC indexes you want
to drop are covering columns rated EXCELLENT/GOOD, that's the confirmation your theory holds
for that table.

## Caveats (honest ones)

- **Sampled distinct ratios are within-sample.** For truly high-cardinality columns the true
  distinct count is underestimated by sampling — but the *within-sample repetitiveness* is
  precisely the compressibility signal, so the verdicts remain sound.
- **TABLESAMPLE is page-based.** Columns correlated with insert order (dates, identities)
  can look slightly *more* repetitive in a page sample than across the whole table. If a
  candidate is borderline, re-run it with `--sample-rows` high enough to force a full scan.
- **Usage DMVs reset on instance restart**; the tool warns when uptime < 7 days.
- **Plan-cache matching is by table-name substring** — verify hits if you have overlapping
  names (`Orders` vs `OrdersArchive`), and remember it only sees currently cached plans.
- The cardinality queries do real reads. Defaults (sampling, MAXDOP 2, read uncommitted,
  20-column batches) are deliberately gentle, but run it off-peak the first time.

## Narrative analysis (new)

Every run now ends with a written DBA-style analysis — printed to the console and saved as
`5_analysis.md`. It's generated by **built-in rules, no AI required**: verdict per table,
data-shape evidence (top repetitive columns, dictionary-pressure risks, rough compressed-size
estimate), workload evidence from the plan cache (aggregation "smoking guns", OPENJSON abuse,
tables nobody reads), index findings (dead indexes, self-duplicating LOB includes,
near-zero-selectivity keys, computed-column CCI blockers), migration notes (ordered CCI build,
delta-store REORGANIZE), and a prioritized punch list with reclaimable-MB estimates.

Optionally, get a second-opinion narrative from any **OpenAI-compatible local LLM**
(saved as `6_llm_analysis.md`):

```bash
# Ollama
dotnet run -- --server SQLPROD01 --database SalesDW --llm-endpoint http://localhost:11434 --llm-model llama3.1
# LM Studio
dotnet run -- --server SQLPROD01 --database SalesDW --llm-endpoint http://localhost:1234 --llm-model whatever-is-loaded
```

The tool sends a compact JSON summary (table stats, column cardinality, indexes, top 5 queries)
plus the rule-based findings as grounding. If the endpoint fails, the
rule-based analysis still stands — the LLM is strictly additive. Note that query text is sent
to the endpoint, so keep it to servers you control if statements may contain sensitive literals.

## Health check (optional, new)

Beyond columnstore candidacy, `--health-check` runs a much broader "is this environment okay"
pass, folding in well-known community diagnostic tooling plus a set of native checks. Off by
default and fully additive — nothing here changes default behavior or output.

```bash
dotnet run -- --server SQLPROD01 --database SalesDW --health-check
```

**Community tools it detects (and runs, if already installed):**

- [Brent Ozar's First Responder Kit](https://github.com/BrentOzarULTD/SQL-Server-First-Responder-Kit)
  (MIT licensed) — `sp_Blitz` (overall health), `sp_BlitzIndex` (index health), `sp_BlitzCache`
  (top resource-consuming cached plans), `sp_BlitzFirst` (wait stats/perfmon snapshot). Where the
  upstream proc supports it, results are scoped to `--database` (`sp_BlitzIndex`/`sp_BlitzCache`/
  `sp_BlitzLock` via `@DatabaseName`, `sp_BlitzFirst`'s plan-cache portion via
  `@FilterPlansByDatabase`) so you don't get a pile of unrelated-database noise; `sp_Blitz` and
  `sp_BlitzBackups` have no such parameter upstream and stay instance-wide by design.
  `sp_BlitzFirst` always runs a fast instant snapshot and, by default, *also* captures a
  supplementary 30-second live sample (`--blitzfirst-seconds N` to change the duration, `0` to
  skip it) — the one place in this stage that adds real wall-clock time by default. `sp_BlitzLock`/
  `sp_BlitzBackups` are available via `--include-blitzlock`/`--include-blitzbackups`.
- [Ola Hallengren's Maintenance Solution](https://ola.hallengren.com/) — detects the
  `DatabaseBackup`/`DatabaseIntegrityCheck`/`IndexOptimize` SQL Agent jobs and reports
  enabled/disabled state, last-run outcome, and next scheduled run. Detection only, no EXEC needed.
- [sp_WhoIsActive](https://github.com/amachanic/sp_whoisactive) (Adam Machanic, GPLv3) —
  **presence/version detection only, never executed.** It's a live-snapshot-of-right-now tool;
  running it inside a batch report would produce one meaningless point-in-time reading.

If a tool isn't installed, the report says so and links to the upstream project. It does **not**
silently skip the finding.

**Native checks** (zero external dependency, same read-only pattern as the rest of the tool):
top wait stats, last known-good `CHECKDB` per database, backup recency (including a "FULL
recovery model with no recent log backup" gap flag — a silent point-in-time-recovery hole),
tempdb file count/config, common `sp_configure`/database-flag smells (MAXDOP, cost threshold
for parallelism, max server memory, auto-shrink/auto-close), and recent Agent job failures.

**Availability Group / replication health** — actual sync-state and lag, not just a topology
count: per-replica connection/health state, per-database sync state, redo/log-send queue depth,
and seconds of lag (`sys.dm_hadr_*`). Flags suspended data movement, unhealthy replicas, and
growing redo queues. Legacy transactional/snapshot/merge replication gets lighter treatment —
recent error surfacing from the distributor only, not a full latency deep-dive.

**"Leave with a good conscience" inventory** — the parts of this aimed specifically at
knowledge transfer, not just diagnostics:

- **Job ownership audit** — flags SQL Agent jobs owned by what looks like a personal login
  rather than a service account. This is the single biggest landmine for a departing
  employee: if that login gets disabled, every job it owns can silently start failing.
- **Security/access inventory** — sysadmin members, disabled-but-still-present logins,
  orphaned database users, and linked servers (with a prompt: "what depends on this, and is
  it still needed?").
- **Topology rollup** — replication/CDC/Availability Group participation, server-wide.
- **"Fill in before you go"** — a printable list of every finding flagged as needing the
  departing owner's own knowledge (unexplained linked servers, personally-owned jobs), with
  blank lines for handwritten answers.
- A plain-English glossary appendix in `report.html`, so a non-DBA manager can actually read it.

### Permissions (read this before enabling)

The base tool needs `VIEW SERVER STATE` + database read. `--health-check` adds: read access to
`master` (tool detection) and `msdb` (Ola Hallengren jobs, backup history, job failures), plus
EXEC rights on whatever FRK procs are already installed. Every check is independently
non-fatal — if a permission is missing, that one check reports "insufficient permission,
skipping" and the run continues.

### Auto-install (opt-in, off by default, reads this carefully)

`--install-missing-tools` (only takes effect combined with `--health-check`) offers to install
whichever of FRK / Ola Hallengren's solution are missing. This is the one feature that changes
the tool's read-only posture — it needs `CREATE PROCEDURE` (typically in `master`) and SQL
Agent job creation rights. Safety rails, all mandatory, none skippable:

1. The exact component, target database, and pinned version are printed before anything happens.
2. Requires typing `YES` at an interactive prompt — refuses to proceed if input is piped/redirected.
3. The script is downloaded from a **pinned URL/version** (never "latest") and its SHA-256 is
   verified against a checksum baked into `InstallSources.cs` before anything is executed. A
   mismatch aborts with no changes made.
4. Every install attempt (confirmed, skipped, succeeded, or failed) is recorded in
   `health_check.json` and `report.html` as an audit trail.

Brent Ozar's own 2026 First Responder Kit release notes explicitly warn against auto-fetching
and running code from the internet against SQL Server — "that's how supply chain attacks
happen." This feature exists because it was explicitly requested, but the pinning + checksum +
confirmation model above is there specifically to close off that exact risk. When in doubt,
skip this flag and install FRK/Ola Hallengren yourself from the linked projects above.

## Permissions inventory (optional, new)

A standalone security checkup for sysadmins who need to keep an inventory of who has access to
what across an entire SQL Server instance. Unlike everything else in this tool, `--permissions-report`
runs **instead of** the columnstore analysis, not alongside it — `--database` is not required
(and is ignored if given), because the whole point is scanning *every* database, not one.

```bash
dotnet run -- --server SQLPROD01 --permissions-report
```

**What it reports:**

- Every server-level login: disabled state, server role memberships (sysadmin, securityadmin, etc.).
- Every database user in every online user database on the instance: which login it maps to,
  which database roles it's in, and whether it's **orphaned** (a SQL-authenticated user with no
  matching server login — usually left behind after a login was dropped or the database was
  restored elsewhere; contained-database users are correctly excluded from this check, since
  they're login-less by design).
- Every **explicit** database/schema/object-level `GRANT`/`DENY` (fixed database roles' built-in
  permissions are implicit and never show up here, so this is naturally just the custom, audit-worthy
  grants — no extra filtering needed to cut noise).

**Flagged automatically:**

- Disabled logins that **still have live database access** — the login is disabled at the server
  level, but a database user still maps to it in one or more databases. This is the single
  biggest "account lingering after someone left" signal.
- Orphaned database users, listed per database.
- Risky explicit grants: `CONTROL`/`ALTER`/`TAKE OWNERSHIP` on an object, or anything granted
  directly to the `public` role (which applies to every user in the database).

**Output** (all in `--output`, same folder as everything else): `permissions_server_principals.csv`,
`permissions_database_users.csv`, `permissions_object_grants.csv` (the full, unfiltered detail),
`permissions_report.json` (everything, machine-readable), `permissions_analysis.md` (exec-level
summary — counts, flagged findings, a per-database table; not a full row dump, that's what the
CSVs/JSON are for), and `permissions_report.html` (self-contained dashboard, `--no-html-report`
to skip).

### Permissions required (read this before running)

This needs enough read access to see **every** database's principals and permissions — in
practice that means `sysadmin`, or `securityadmin` plus `VIEW DEFINITION` in every database. A
normal least-privilege login will only see a partial picture. Databases the connecting login
can't see into are skipped with a warning, not a fatal error — the report still writes with
whatever it could reach, and lists what it couldn't in a Warnings section.

## Self-test (optional, new)

A strictly **read-only** diagnostic pass, meant to be run manually against a real instance and
the resulting file(s) shared for review — useful when you want confidence this tool actually
works against your specific SQL Server version/config before relying on it, without needing a
dedicated test cluster.

```bash
dotnet run -- --server SQLPROD01 --self-test
```

- Exercises every code path in the tool: a small sample of columnstore tables (capped at 3, for
  speed, including the Query Store enrichment), every health-check native check (including
  Availability Group health), First Responder Kit detection plus **instant-only** execution of
  whatever's installed (never the 30-second live sample, regardless of `--blitzfirst-seconds`),
  Ola Hallengren detection, and the full permissions inventory.
- **Every single step is a `SELECT`, a read-only `DBCC` command, or an `EXEC` of an
  already-installed read-only diagnostic proc.** Nothing here ever creates, modifies, or deletes
  anything, and it never touches the auto-install flow — it refuses to even start if
  `--install-missing-tools` is also passed.
- Each step's summary captures row/column **counts and shape only** — never actual data values —
  so the output file is safe to hand back to whoever's helping build or debug this tool, even
  from a production instance.
- `--database` is not required (every check that's normally database-scoped either loops every
  database or is instance-wide by nature, same as `--permissions-report`).
- Writes `self_test_results.json` (precise, for someone to read closely) and
  `self_test_results.md` (a quick pass/fail table) to `--output`, plus a console summary.

Separately, `ColumnstoreAnalyzer.Tests` (xUnit) covers the pure logic that doesn't need a
database at all — FRK result mapping, scoring, permission grouping, CSV escaping, findings
dedup — and runs anywhere with `dotnet test ColumnstoreAnalyzer.Tests`.
