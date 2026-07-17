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
7. Scores every table 0–100 and writes reports

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

| Flag | Default | Meaning |
|------|---------|---------|
| `--min-rows` | 1,000,000 | Table qualifies at this row count... |
| `--min-mb` | 500 | ...or at this size (either passes) |
| `--top` | 25 | Max tables fully analyzed |
| `--sample-rows` | 1,000,000 | Target sample size for cardinality measurement |
| `--column-batch` | 20 | Columns per cardinality query |
| `--timeout` | 600 | Per-query timeout (seconds) |
| `--max-queries` | 20 | Cached queries kept per table |
| `--output` | timestamped folder | Report destination |
| `--no-trust-cert` | off | Disable TrustServerCertificate |

## Output

Console shows a ranked summary; the output folder gets:

- `1_ranked_candidates.csv` — one row per table: score, data-shape metrics
  (% low-cardinality columns, median duplication factor, dictionary-pressure column count),
  workload metrics (scan %, write %, lock waits), NC index bloat ratio, notes
- `2_column_analysis.csv` — **the duplicate-data report**: per column, distinct ratio,
  duplication factor (avg repeats per value), null count, avg byte length, and a verdict
  (EXCELLENT / GOOD / NEUTRAL / POOR / BAD-dictionary-pressure)
- `3_index_inventory.csv` — every index with size and columns; wide and dead indexes flagged
- `4_referencing_queries.csv` — cached statements per table, ranked by logical reads
- `5_analysis.md` — narrative analysis of each table, with punch-list of reclaimable MB
- `6_llm_analysis.md` — optional second-opinion narrative from a local LLM (if endpoint provided)
- `full_report.json` — everything, machine-readable

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
