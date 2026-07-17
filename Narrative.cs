using System.Text;

namespace ColumnstoreAnalyzer;

/// <summary>
/// Rule-based narrative generator. Encodes the reasoning patterns a DBA applies when
/// reading the raw report: verdicts per table, evidence from data shape + workload,
/// index-level findings (dead indexes, self-duplicating LOB includes, computed-column
/// blockers), and a prioritized punch list. No AI required.
/// </summary>
public static class Narrative
{
    private sealed record Finding(int Priority, string Text, double ReclaimMb);

    public static string Build(AnalyzerOptions opt, List<TableInfo> tables)
    {
        var sb = new StringBuilder();
        var punchList = new List<Finding>();

        sb.AppendLine($"# Columnstore Analysis — {opt.Database} on {opt.Server}");
        sb.AppendLine(
            $"_Generated {DateTime.Now:yyyy-MM-dd HH:mm}. Rule-based analysis; verify top findings in non-prod before acting._");
        sb.AppendLine();

        // ---- Cross-report data-quality checks ------------------------------------------
        var allUsageZero = tables.All(t => t.UserSeeks + t.UserScans + t.UserLookups + t.UserUpdates == 0);
        var cacheShowsActivity = tables.Any(t => t.ReferencingQueries.Count > 0);
        if (allUsageZero && cacheShowsActivity)
        {
            sb.AppendLine(
                "> **Data-quality flag:** every table shows zero index usage, yet the plan cache proves recent " +
                "activity. The usage DMVs almost certainly reset recently (instance restart). The workload half " +
                "of the scoring contributed nothing — scores below are driven by data shape and size only. " +
                "Re-run after the instance has a week of uptime.");
            sb.AppendLine();
        }

        // ---- Per-table analysis ---------------------------------------------------------
        foreach (var t in tables.OrderByDescending(x => x.CandidacyScore))
        {
            sb.AppendLine($"## {t.FullName} — {Verdict(t)}");
            sb.AppendLine();
            sb.AppendLine($"*Score {t.CandidacyScore:0.0} | {t.RowCount:N0} rows | {t.TotalSizeMb:N0} MB total " +
                          $"({t.BaseDataMb:N0} MB data + {t.NonclusteredMb:N0} MB NC indexes) | " +
                          $"~{t.PotentialFullRowgroups:0.#} potential rowgroups*");
            sb.AppendLine();

            if (t.HasColumnstore)
            {
                sb.AppendLine("Already has a columnstore index — excluded from candidacy analysis.");
                sb.AppendLine();
                continue;
            }

            WriteDataShape(sb, t);
            WriteWorkload(sb, t, punchList);
            WriteIndexFindings(sb, t, punchList);
            WriteRecommendation(sb, t, punchList);
            sb.AppendLine();
        }

        // ---- Punch list ------------------------------------------------------------------
        if (punchList.Count <= 0) return sb.ToString();

        sb.AppendLine("## Punch list (prioritized)");
        sb.AppendLine();
        var n = 1;
        foreach (var f in punchList.OrderBy(f => f.Priority).ThenByDescending(f => f.ReclaimMb))
        {
            var reclaim = f.ReclaimMb > 0 ? $" (~{f.ReclaimMb:N0} MB)" : "";
            sb.AppendLine($"{n++}. {f.Text}{reclaim}");
        }

        var totalReclaim = punchList.Sum(f => f.ReclaimMb);
        if (!(totalReclaim > 100)) return sb.ToString();
        sb.AppendLine();
        sb.AppendLine($"Estimated total reclaimable/compressible: **~{totalReclaim:N0} MB** " +
                      "(rough estimates; validate in non-prod).");

        return sb.ToString();
    }

    // ======================================================================================

    private static string Verdict(TableInfo t)
    {
        if (t.HasColumnstore) return "already columnstore";
        var tooSmall = t.PotentialFullRowgroups < 3;
        var aggEvidence = TopUserQueries(t).Any(q => IsAggregation(q.StatementText));
        var insertOnly = IsInsertOnly(t);

        return t.CandidacyScore switch
        {
            >= 55 => "STRONG COLUMNSTORE CANDIDATE",
            >= 35 => aggEvidence
                ? "GENUINE COLUMNSTORE CANDIDATE (workload evidence supports it)"
                : "PROMISING CANDIDATE — test it",
            >= 15 when insertOnly && t.PctMediumCardinalityColumns >= 50 && !tooSmall =>
                "MARGINAL — compression win possible, low priority",
            >= 20 => "MARGINAL",
            _ => tooSmall ? "NOT A CANDIDATE (too small / wrong shape)" : "NOT A COLUMNSTORE CANDIDATE"
        };
    }

    private static void WriteDataShape(StringBuilder sb, TableInfo t)
    {
        var done = t.Columns.Where(c => c is { Analyzed: true, SampleRows: > 0 }).ToList();
        if (done.Count == 0)
        {
            sb.AppendLine("Data cardinality was not measured for this table.");
            sb.AppendLine();
            return;
        }

        var stars = done.Where(c => c.DuplicationFactor >= 100)
            .OrderByDescending(c => c.DuplicationFactor).Take(4).ToList();
        var good = done.Where(c => c.DuplicationFactor is >= 10 and < 100).ToList();
        var unique = done.Where(c => c.DistinctRatio >= 0.9).Select(c => c.ColumnName).ToList();

        sb.Append("**Data shape:** ");
        if (stars.Count > 0)
        {
            sb.Append("highly repetitive — " + string.Join(", ",
                stars.Select(c =>
                    $"`{c.ColumnName}` ({c.DistinctValues:N0} distinct, {c.DuplicationFactor:N0}x repeats)")));
            if (good.Count > 0) sb.Append($", plus {good.Count} more column(s) at 10x+ duplication");
            sb.Append('.');
        }
        else if (good.Count > 0)
        {
            sb.Append($"moderately repetitive — {good.Count} column(s) with 10x+ duplication.");
        }
        else
        {
            sb.Append("mostly high-cardinality — little for dictionary/run-length compression to work with.");
        }

        if (unique.Count > 0)
            sb.Append($" Near-unique: {string.Join(", ", unique.Select(u => $"`{u}`"))} (normal for keys/timestamps).");
        sb.AppendLine();

        // Biggest compressible payload: wide repetitive string columns
        var jackpot = done.Where(c => c is { IsStringType: true, AvgByteLength: >= 20, DuplicationFactor: >= 50 })
            .OrderByDescending(c => c.AvgByteLength * t.RowCount).FirstOrDefault();
        if (jackpot != null)
        {
            var mb = jackpot.AvgByteLength * t.RowCount / 1024.0 / 1024.0;
            sb.AppendLine($"`{jackpot.ColumnName}` alone is ~{mb:N0} MB of the table " +
                          $"(avg {jackpot.AvgByteLength:0} bytes x {t.RowCount:N0} rows) with only " +
                          $"{jackpot.DistinctValues:N0} distinct values — it would dictionary-compress to almost nothing.");
        }

        var dict = done.Where(c => c.DictionaryPressure).ToList();
        if (dict.Count > 0)
            sb.AppendLine($"Dictionary-pressure risk: {string.Join(", ", dict.Select(c => $"`{c.ColumnName}`"))} " +
                          "(near-unique wide strings trim rowgroups and hurt compression).");

        var estMb = EstimateCompressedMb(t, done);
        if (estMb > 0 && t.BaseDataMb > 50 && estMb < (double)t.BaseDataMb * 0.6)
            sb.AppendLine(
                $"Very rough clustered-columnstore size estimate: **~{estMb:N0} MB** vs {t.BaseDataMb:N0} MB rowstore data today.");
        sb.AppendLine();
    }

    private static void WriteWorkload(StringBuilder sb, TableInfo t, List<Finding> punch)
    {
        var parts = new List<string>();
        var insertOnly = IsInsertOnly(t);

        if (t.UserSeeks + t.UserScans + t.UserLookups + t.UserUpdates > 0)
        {
            parts.Add(
                $"{t.UserSeeks:N0} seeks / {t.UserScans:N0} scans / {t.UserLookups:N0} lookups / {t.UserUpdates:N0} write ops since restart");
            if (insertOnly && t.LeafInserts > 0)
                parts.Add("writes are pure appends (zero updates/deletes) — the columnstore-friendly pattern; " +
                          "any 'write-heavy' caution in the score is a false alarm here");
            else if (t.UpdateShareOfWritesPct >= 20)
                parts.Add(
                    $"UPDATEs are {t.UpdateShareOfWritesPct:0}% of writes — the expensive operation on columnstore");
            if (t.UserLookups > 100_000)
                parts.Add($"{t.UserLookups:N0} key lookups show the NC indexes don't fully cover their queries");
            if (t.PageIoLatchWaitMs > 10_000)
                parts.Add(
                    $"{t.PageIoLatchWaitMs / 1000:N0}s of IO-latch waits — IO-bound scans, which columnstore compression directly relieves");
        }

        if (parts.Count > 0)
        {
            sb.AppendLine("**Workload:** " + string.Join("; ", parts) + ".");
        }

        // Query-cache evidence
        var userQueries = TopUserQueries(t);
        var topAgg = userQueries.Where(q => IsAggregation(q.StatementText))
            .OrderByDescending(q => q.TotalLogicalReads).FirstOrDefault();
        var top = userQueries.OrderByDescending(q => q.TotalLogicalReads).FirstOrDefault();

        if (topAgg is { TotalLogicalReads: > 100_000 })
        {
            sb.AppendLine($"**Smoking gun:** an aggregation query ran {topAgg.ExecutionCount:N0} times for " +
                          $"**{topAgg.TotalLogicalReads:N0} reads and {topAgg.TotalCpuMs / 1000:N0}s CPU** — " +
                          "exactly the GROUP-BY/SUM shape that batch-mode columnstore accelerates.\n" +
                          $"```sql\n{Trim(topAgg.StatementText, 3000)}\n```");
        }
        else if (top is { TotalLogicalReads: > 500_000 })
        {
            var perExec = top.ExecutionCount > 0 ? (double)top.TotalLogicalReads / top.ExecutionCount : 0;
            sb.AppendLine();
            sb.AppendLine(
                $"**Heaviest cached query:** {top.ExecutionCount:N0} executions, {top.TotalLogicalReads:N0} reads " +
                $"(~{perExec:N0}/exec), {top.TotalCpuMs / 1000:N0}s CPU:\n\n```sql\n{Trim(top.StatementText, 3000)}\n```");
            if (ContainsIgnoreCase(top.StatementText, "OPENJSON"))
                sb.AppendLine("It shreds JSON with OPENJSON on every run — consider promoting the queried JSON " +
                              "properties to real (or persisted computed) columns instead of re-parsing blobs.");
        }
        else if (userQueries.Count > 0 && userQueries.All(q => IsInsert(q.StatementText)))
        {
            sb.AppendLine("**Notably, nobody reads this table** — only INSERTs appear in the plan cache. " +
                          "Before indexing it, consider whether a retention/archive policy is the better fix.");
        }

        sb.AppendLine();
    }

    private static void WriteIndexFindings(StringBuilder sb, TableInfo t, List<Finding> punch)
    {
        var lobCols = t.Columns.Where(c => c.IsLob).Select(c => c.ColumnName).ToList();
        var computedCols = t.Columns.Where(c => c.IsComputed).Select(c => c.ColumnName).ToList();
        var findings = new List<string>();
        var isHeap = !t.Indexes.Any(i => i.TypeDesc.Contains("CLUSTERED", StringComparison.OrdinalIgnoreCase)
                                         && !i.TypeDesc.Contains("NONCLUSTERED", StringComparison.OrdinalIgnoreCase));
        if (isHeap && t.Indexes.Count == 0)
            findings.Add("the table is a **heap** with no indexes at all — every read is a full scan");

        double deadMb = 0;
        foreach (var i in t.Indexes.Where(x => x.TypeDesc == "NONCLUSTERED"))
        {
            var reads = i.UserSeeks + i.UserScans + i.UserLookups;

            var dead = (reads == 0 && i.UserUpdates > 0) ||
                       (reads < 20 && i.UserUpdates > 10_000);
            if (dead)
            {
                deadMb += (double)i.SizeMb;
                findings.Add($"`{i.IndexName}` ({i.SizeMb:N0} MB) is **essentially dead**: {reads:N0} reads vs " +
                             $"{i.UserUpdates:N0} maintenance writes — drop it regardless of the columnstore decision");
            }

            var lobIncluded = SplitCols(i.IncludedColumns).Intersect(lobCols, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (lobIncluded.Count > 0 && i.SizeMb >= t.BaseDataMb * 0.5m)
            {
                findings.Add($"`{i.IndexName}` ({i.SizeMb:N0} MB) INCLUDEs the LOB column(s) " +
                             $"{string.Join(", ", lobIncluded.Select(c => $"`{c}`"))}, duplicating the payload — " +
                             "rebuild it without the LOB include");
                punch.Add(new Finding(1, $"{t.FullName}: rebuild `{i.IndexName}` without the LOB INCLUDE",
                    (double)i.SizeMb * 0.9));
            }

            var firstKey = SplitCols(i.KeyColumns).FirstOrDefault();
            var keyCol = firstKey == null
                ? null
                : t.Columns.FirstOrDefault(c =>
                    c.Analyzed && c.ColumnName.Equals(firstKey, StringComparison.OrdinalIgnoreCase));
            if (keyCol is { DistinctValues: <= 10 } && !dead && lobIncluded.Count == 0)
                findings.Add($"`{i.IndexName}` is keyed on `{keyCol.ColumnName}` which has only " +
                             $"{keyCol.DistinctValues} distinct value(s) — nearly zero selectivity as a leading key");

            var computedKeys = SplitCols(i.KeyColumns).Intersect(computedCols, StringComparer.OrdinalIgnoreCase)
                .ToList();

            switch (dead)
            {
                case true when computedKeys.Count > 0:
                    findings.Add(
                        $"`{i.IndexName}` is keyed on computed column(s) {string.Join(", ", computedKeys.Select(c => $"`{c}`"))} " +
                        "— which exist only to feed this dead index");
                    break;
                case false when i.IncludedColumnCount >= 5:
                    findings.Add(
                        $"`{i.IndexName}` ({i.SizeMb:N0} MB) is wide ({i.IncludedColumnCount} included columns) — " +
                        "the kind of covering index a columnstore replaces");
                    break;
            }
        }

        if (deadMb > 0)
            punch.Add(new Finding(1, $"{t.FullName}: drop the dead nonclustered index(es) — zero risk, immediate win",
                deadMb));

        if (findings.Count > 0)
        {
            sb.AppendLine("**Index findings:**");
            foreach (var f in findings) sb.AppendLine($"- {f}");
            sb.AppendLine();
        }
    }

    private static void WriteRecommendation(StringBuilder sb, TableInfo t, List<Finding> punch)
    {
        var computedCols = t.Columns.Where(c => c.IsComputed).Select(c => c.ColumnName).ToList();
        var insertOnly = IsInsertOnly(t);
        var tooSmall = t.PotentialFullRowgroups < 3;
        var dateCol = t.Columns.FirstOrDefault(c => c.Analyzed &&
                                                    (c.DataType.StartsWith("date") || c.DataType == "datetime2" ||
                                                     c.DataType == "datetime"));

        sb.Append("**Recommendation:** ");
        switch (t.CandidacyScore)
        {
            case >= 35:
            {
                var steps = new List<string>();
                if (computedCols.Count > 0)
                    steps.Add(
                        $"drop the computed column(s) {string.Join(", ", computedCols.Select(c => $"`{c}`"))} first " +
                        "(clustered columnstore does not support computed columns — check application references)");
                if (dateCol != null)
                    steps.Add($"build the CCI with data ordered by `{dateCol.ColumnName}` " +
                              "(rebuild the clustered B-tree on it first, then `CREATE CLUSTERED COLUMNSTORE ... WITH (DROP_EXISTING = ON))` " +
                              "for segment elimination on date-range queries");
                if (insertOnly)
                    steps.Add("inserts land in the delta store — schedule a periodic `ALTER INDEX ... REORGANIZE`");
                if (t.WritePct >= 25 && !insertOnly)
                    steps.Add(
                        "given the write mix, test a NONCLUSTERED columnstore first (keeps the rowstore PK, still gets batch mode)");

                sb.AppendLine("convert to clustered columnstore and retire the redundant B-tree indexes. " +
                              (steps.Count > 0 ? "Migration notes: " + string.Join("; ", steps) + "." : ""));
                punch.Add(new Finding(2,
                    $"{t.FullName}: plan the columnstore conversion (test the heaviest query before/after)",
                    Math.Max(0,
                        (double)t.BaseDataMb - EstimateCompressedMb(t, t.Columns.Where(c => c.Analyzed).ToList()))));
                break;
            }
            case >= 20 when insertOnly && !tooSmall:
                sb.AppendLine(
                    "possible compression win but low absolute payoff — revisit after the higher-priority items.");
                break;
            default:
                sb.AppendLine("leave the storage model as-is; act on the index findings above if any. " +
                              (tooSmall ? "The table is too small for columnstore to form meaningful rowgroups. " : "") +
                              "This is B-tree/application-fix territory, not columnstore.");
                break;
        }
    }

    // ---- helpers -------------------------------------------------------------------------

    private static List<CachedQuery> TopUserQueries(TableInfo t) =>
        t.ReferencingQueries.Where(q => !IsAnalyzerQuery(q.StatementText)).ToList();

    private static bool IsAnalyzerQuery(string s) =>
        ContainsIgnoreCase(s, "AS __rows") || ContainsIgnoreCase(s, "COUNT_BIG(DISTINCT");

    private static bool IsAggregation(string s) =>
        ContainsIgnoreCase(s, "SELECT") &&
        (ContainsIgnoreCase(s, "GROUP BY") || ContainsIgnoreCase(s, "SUM(") ||
         ContainsIgnoreCase(s, "AVG(") || ContainsIgnoreCase(s, "COUNT(")) &&
        !IsInsert(s);

    private static bool IsInsert(string s) =>
        s.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase);

    private static bool IsInsertOnly(TableInfo t) =>
        t is { LeafInserts: > 0, LeafUpdates: 0, LeafDeletes: 0 };

    private static bool ContainsIgnoreCase(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static List<string> SplitCols(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Trim(string s, int max)
    {
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..max] + "...";
    }

    /// <summary>Very rough CCI size estimate from per-column duplication factors.</summary>
    private static double EstimateCompressedMb(TableInfo t, List<ColumnStat> analyzed)
    {
        var bytes = (from c in analyzed.Where(c => c.SampleRows > 0)
            let factor = c.DuplicationFactor >= 1000 ? 0.02
                : c.DuplicationFactor >= 100 ? 0.05
                : c.DuplicationFactor >= 10 ? 0.15 : 0.5
            select c.AvgByteLength * t.RowCount * factor).Sum();

        return bytes / 1024.0 / 1024.0;
    }
}