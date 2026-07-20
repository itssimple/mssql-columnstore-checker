# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A .NET 10 console tool (`ColumnstoreAnalyzer`) that connects to a live SQL Server database and ranks its large tables by how good a candidate each is for a columnstore index. Unlike DMV-only advisors, it **reads the actual column data** to measure repetitiveness, because columnstore compression (dictionary + run-length encoding) depends on duplicate values. See `README.md` for the full domain rationale and the scoring philosophy.

## Build & run

```bash
dotnet build -c Release
dotnet run -c Release -- --server SQLPROD01 --database SalesDW              # Windows integrated auth
dotnet run -c Release -- --server SQLPROD01 --database SalesDW --user analyst  # SQL auth, prompts for masked password
dotnet publish -c Release -r win-x64 --self-contained                       # standalone exe
```

There are no tests, no linter config, and no CI in this repo. `--help` prints full usage. Target DB permissions required: `VIEW SERVER STATE` + read on the database (the opt-in health-check stage widens this — see below).

## Architecture

The program is a **single linear pipeline** with no DI, no async, and one class per stage. `Program.Run` (`Program.cs`) is the orchestrator and reads top-to-bottom as the whole flow:

1. `Analyzer.DiscoverTables` — finds large user tables (size/rowcount thresholds) with workload + columnstore/replication flags in one query.
2. If `--health-check` was passed: `HealthCheckRunner.Run` (see "Health check stage" below) — runs right after server-vitals, before table discovery.
3. Per table, in order: `LoadOperationalStats`, `LoadIndexes`, `LoadColumns`, then `AnalyzeColumnCardinality` (skipped if the table already has a columnstore index).
4. `MatchPlanCacheQueries` — one pass over the whole plan cache, statements matched to tables **client-side by table-name substring**.
5. `Scoring.Score` per table → 0–100 candidacy score.
6. `ReportWriter.WriteAll` → CSVs + `full_report.json` (+ `7_health_check_findings.csv`/`health_check.json` if health-check ran); `Narrative.Build` → `5_analysis.md`; optional `LlmClient.GenerateNarrative` → `6_llm_analysis.md`; `HtmlReportWriter.Write` → `report.html` (self-contained dashboard, on by default, `--no-html-report` to skip).

Key module responsibilities:
- **`Sql.cs`** — every columnstore-analysis T-SQL query as a `const string`. All DB queries live here; `Analyzer.cs` only executes them and maps rows to models. Change SQL here, not inline.
- **`Analyzer.cs`** — the columnstore-analysis DB-touching class. Opens a fresh `SqlConnection` per call (`Open()` → `ConnectionFactory.Open(opt)`). `AnalyzeColumnCardinality` is the heart: batches ~20 columns per query (`COUNT_BIG(DISTINCT)`, null count, avg `DATALENGTH`) under `READ UNCOMMITTED` + `MAXDOP 2`, using `TABLESAMPLE SYSTEM` when a table exceeds `SampleTargetRows * 2`.
- **`ConnectionFactory.cs`** — the one place the `SqlConnectionStringBuilder`/auth logic lives, shared by `Analyzer` and `HealthCheckAnalyzer` so it can't drift between the two DB-touching classes.
- **`Models.cs`** — all data types. `TableInfo` / `ColumnStat` hold raw DMV values as `init`/`set` properties and expose **derived metrics as computed getters** (`ScanPct`, `WritePct`, `DistinctRatio`, `DuplicationFactor`, `DictionaryPressure`, `Verdict`, `NcIndexBloatRatio`, `PotentialFullRowgroups`). Put derived logic in these getters, not in the analyzer or scorer. Also holds the health-check types (`HealthCheckFinding`, `ComponentStatus`, `MaintenanceJobStatus`, `DynamicResultSet`, `HealthCheckResult`, `InstallAction`) and their `AnalyzerOptions` flags.
- **`Scoring.cs`** — pure function `Score(TableInfo)` mutating `CandidacyScore` + `AssessmentNotes`. The weighting model (data-shape up to 40 pts, size 15, scan 20, NC-index bloat 15, contention 10, minus write/update/dictionary/LOB penalties) is the product's opinion — keep it in sync with the rationale in the class doc-comment and README. Scoped to columnstore candidacy only — health-check findings live in `HealthCheckFinding.Severity`, not here.
- **`Narrative.cs`** — rule-based (no AI) DBA-style prose report for columnstore candidacy; the LLM output is strictly additive grounding, never required. Not extended for health-check findings (those get their own dashboard in `HtmlReportWriter.cs`) — avoid scope creep into a second markdown narrative generator.
- **`LlmClient.cs`** — talks to any OpenAI-compatible endpoint (Ollama/LM Studio). Purely optional; failures are caught and non-fatal. This is the reference pattern the health-check stage's third-party integrations (FRK/Ola Hallengren) follow: opt-in, off by default, non-fatal.
- **`ReportWriter.cs`** — the numbered CSVs, `full_report.json`, and (when health-check ran) `7_health_check_findings.csv` + a separate `health_check.json` (kept separate so `full_report.json`'s existing schema stays untouched for anyone already parsing it).
- **`HtmlReportWriter.cs`** — self-contained `report.html` dashboard (inline CSS/JS only, no CDN — must open with zero network access). An *additional* artifact, not a replacement for the CSVs/markdown. Hand-rolled `StringBuilder` HTML, same style as `Narrative.cs`; uses `System.Net.WebUtility.HtmlEncode` for anything embedded (query text, finding details) since that content isn't guaranteed HTML-safe.
- **`PasswordInput.cs`** — masked interactive password prompt, stdin-redirect aware. `HealthCheckRunner`'s install-flow confirmation prompt follows the same stdin-redirect-aware pattern (abort rather than silently proceed when non-interactive).

### Health check stage (opt-in, `--health-check`, off by default)

A second, parallel pipeline for broader SQL Server best-practices checks (Brent Ozar's First Responder Kit, Ola Hallengren's Maintenance Solution, sp_WhoIsActive, plus native checks and a "tribal knowledge" inventory), designed so a departing DBA/engineer's institutional knowledge survives them. Entirely additive — default behavior, output, and permissions are unchanged unless `--health-check` is passed.

- **`SqlHealthCheck.cs`** / **`HealthCheckAnalyzer.cs`** — the health-check equivalent of `Sql.cs`/`Analyzer.cs`. `HealthCheckAnalyzer` is the second (and only other) DB-touching class; same shape (`Open()`, one method per check, no internal catching — that's the runner's job). Includes `DynamicResultSet`/`ReadAllDynamic` for capturing EXEC'd stored procs whose result schema isn't known in advance (FRK procs can also return >1 result set — `ReadAllDynamic` loops `NextResult()`).
- **`BlitzAdapter.cs`** — pure mapper, no DB access (like `Scoring.cs`), `DynamicResultSet → List<HealthCheckFinding>`. Defensive by design: unrecognized/renamed columns degrade to a raw-row `Info` finding rather than throwing, since FRK's exact schema can change between releases. The `Priority`→severity breakpoints are a best-effort reading of Ozar's convention, not verified against a live run — sanity-check before trusting them blindly.
- **`HealthCheckRunner.cs`** — orchestrates the whole stage with the same non-fatal per-step console pattern as `Program.Run`. `sp_WhoIsActive` is **detect-only, never executed** — it's a live-snapshot tool, meaningless in a static report. `sp_BlitzIndex`/`sp_BlitzCache`/`sp_BlitzLock` are scoped to `--database` via `@DatabaseName` (verified against the pinned FRK source — `sp_Blitz` and `sp_BlitzBackups` have no such parameter upstream and stay instance-wide by design); `sp_BlitzFirst` has no `@DatabaseName` either but takes `@FilterPlansByDatabase` for its embedded plan-cache portion. `sp_BlitzFirst` always runs its instant snapshot (`@SinceStartup=1`) and, by default, *also* captures a supplementary 30-second live `@Seconds=` sample (`--blitzfirst-seconds N` to change the duration, `0` to skip it) — this is the one place in the health-check stage that intentionally burns real wall-clock time by default; keep it clearly labeled in the console progress line.
- **`InstallSources.cs`** — pinned metadata (exact release tag/URL + verified SHA-256) for the opt-in auto-install feature (`--health-check --install-missing-tools`). **Never "fetch latest"** — this is the one part of the tool that can write to the target instance (`CREATE PROCEDURE`, SQL Agent jobs), so it only runs a script whose content has been reviewed once and pinned, gated behind interactive confirmation, and aborts safely under piped/non-interactive input. Brent Ozar's own 2026 FRK release notes explicitly warn against auto-fetching-and-running code from the internet against SQL Server for exactly this reason — re-verify and update the checksum here deliberately when intentionally upgrading, never loosen this without re-adding equivalent rigor.

## Conventions

- **Identifier escaping**: any table/column name interpolated into SQL goes through `Analyzer.Escape` (`internal static`, `[...]` with `]` doubled) — shared by both DB-touching classes. Never interpolate a raw identifier into a query string. (DBCC DBINFO's database-name argument is a string *literal*, not a bracketed identifier — that one uses single-quote doubling instead, see `HealthCheckAnalyzer.RunCheckdbAge`.)
- **Non-fatal by design**: per-table analysis failures, plan-cache scrape, the LLM step, and every health-check step all catch and continue — one bad check must not abort the run. Preserve this.
- **DMV caveats are load-bearing**: sampled distinct counts are within-sample only; usage stats reset on instance restart (tool warns when uptime < 7 days); plan-cache matching is substring-based. Don't "fix" these into implying more precision than they have.
- File-scoped namespace `ColumnstoreAnalyzer`, nullable + implicit usings enabled, collection expressions (`[]`), switch expressions. Match the existing terse style.
