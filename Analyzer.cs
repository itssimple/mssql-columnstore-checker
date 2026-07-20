using Microsoft.Data.SqlClient;

namespace ColumnstoreAnalyzer;

public sealed class Analyzer
{
    private readonly AnalyzerOptions _opt;

    public Analyzer(AnalyzerOptions opt)
    {
        _opt = opt;
    }

    private SqlConnection Open() => ConnectionFactory.Open(_opt);

    public (int majorVersion, DateTime startTime, int uptimeDays) GetServerInfo()
    {
        using var conn = Open();
        using var cmd = new SqlCommand(Sql.ServerInfo, conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        using var r = cmd.ExecuteReader();
        r.Read();
        return (int.Parse(r.GetString(0)), r.GetDateTime(1), r.GetInt32(2));
    }

    public List<TableInfo> DiscoverTables()
    {
        var tables = new List<TableInfo>();
        using var conn = Open();
        using var cmd = new SqlCommand(Sql.DiscoverTables, conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        cmd.Parameters.AddWithValue("@MinRowCount", _opt.MinRowCount);
        cmd.Parameters.AddWithValue("@MinTableSizeMB", _opt.MinTableSizeMb);
        cmd.Parameters.AddWithValue("@TopN", _opt.TopNCandidates);

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            tables.Add(new TableInfo
            {
                ObjectId = r.GetInt32(r.GetOrdinal("object_id")),
                SchemaName = r.GetString(r.GetOrdinal("schema_name")),
                TableName = r.GetString(r.GetOrdinal("table_name")),
                RowCount = r.GetInt64(r.GetOrdinal("row_count")),
                TotalSizeMb = r.GetDecimal(r.GetOrdinal("total_size_mb")),
                BaseDataMb = r.GetDecimal(r.GetOrdinal("base_data_mb")),
                NonclusteredMb = r.GetDecimal(r.GetOrdinal("nonclustered_mb")),
                IsReplicated = r.GetInt32(r.GetOrdinal("is_replicated")) == 1,
                IsCdcTracked = r.GetInt32(r.GetOrdinal("is_cdc_tracked")) == 1,
                HasColumnstore = r.GetInt32(r.GetOrdinal("has_columnstore")) == 1,
                UserSeeks = r.GetInt64(r.GetOrdinal("user_seeks")),
                UserScans = r.GetInt64(r.GetOrdinal("user_scans")),
                UserLookups = r.GetInt64(r.GetOrdinal("user_lookups")),
                UserUpdates = r.GetInt64(r.GetOrdinal("user_updates")),
                MissingIndexSuggestions = r.GetInt32(r.GetOrdinal("missing_index_suggestions"))
            });
        }

        return tables;
    }

    public void LoadOperationalStats(TableInfo t)
    {
        using var conn = Open();
        using var cmd = new SqlCommand(Sql.OperationalStats, conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        cmd.Parameters.AddWithValue("@ObjectId", t.ObjectId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return;
        t.RowLockWaitMs = r.GetInt64(0);
        t.PageLockWaitMs = r.GetInt64(1);
        t.PageIoLatchWaitMs = r.GetInt64(2);
        t.LeafInserts = r.GetInt64(3);
        t.LeafUpdates = r.GetInt64(4);
        t.LeafDeletes = r.GetInt64(5);
    }

    public void LoadColumns(TableInfo t)
    {
        using var conn = Open();
        using var cmd = new SqlCommand(Sql.GetColumns, conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        cmd.Parameters.AddWithValue("@ObjectId", t.ObjectId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            t.Columns.Add(new ColumnStat
            {
                ColumnName = r.GetString(0),
                DataType = r.GetString(1),
                MaxLength = r.GetInt16(2),
                IsComputed = r.GetBoolean(3),
                IsLob = r.GetInt32(4) == 1
            });
        }
    }

    public void LoadIndexes(TableInfo t)
    {
        using var conn = Open();
        using var cmd = new SqlCommand(Sql.GetIndexes, conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        cmd.Parameters.AddWithValue("@ObjectId", t.ObjectId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var ix = new IndexInfo
            {
                IndexName = r.IsDBNull(0) ? "(heap)" : r.GetString(0),
                TypeDesc = r.GetString(1),
                SizeMb = r.GetDecimal(2),
                KeyColumnCount = r.GetInt32(3),
                IncludedColumnCount = r.GetInt32(4),
                KeyColumns = r.GetString(5),
                IncludedColumns = r.GetString(6),
                UserSeeks = r.GetInt64(7),
                UserScans = r.GetInt64(8),
                UserLookups = r.GetInt64(9),
                UserUpdates = r.GetInt64(10)
            };

            var reads = ix.UserSeeks + ix.UserScans + ix.UserLookups;
            ix.Note = ix.TypeDesc switch
            {
                "NONCLUSTERED" when reads == 0 && ix.UserUpdates > 0 =>
                    "DROP CANDIDATE: written to, never read (since last restart)",
                "NONCLUSTERED" when ix.IncludedColumnCount >= 5 =>
                    "WIDE: 5+ included columns - likely replaceable by columnstore",
                _ => ix.Note
            };

            t.Indexes.Add(ix);
        }
    }

    /// <summary>
    /// The heart of the tool: measures actual data repetitiveness per column.
    /// Columnstore compression = dictionary + run-length encoding, so columns where
    /// values repeat a lot (low distinct ratio) compress dramatically; near-unique
    /// wide strings do not, and can trim rowgroups via dictionary pressure.
    /// Uses TABLESAMPLE on very large tables to keep the analysis cheap.
    /// </summary>
    public void AnalyzeColumnCardinality(TableInfo t)
    {
        var analyzable = t.Columns
            .Where(c => c is { IsComputed: false, IsLob: false })
            .ToList();

        foreach (var c in t.Columns.Where(c => c.IsComputed))
            c.SkipReason = "skipped: computed column";
        foreach (var c in t.Columns.Where(c => c.IsLob))
            c.SkipReason = "skipped: LOB/unsupported type (also a columnstore concern in itself)";

        if (analyzable.Count == 0 || t.RowCount == 0) return;

        // Decide sampling
        string fromClause;
        if (t.RowCount > _opt.SampleTargetRows * 2)
        {
            var pct = Math.Max(0.1, Math.Min(100.0,
                100.0 * _opt.SampleTargetRows / t.RowCount));
            fromClause = $"FROM {Escape(t.SchemaName)}.{Escape(t.TableName)} " +
                         $"TABLESAMPLE SYSTEM ({pct.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)} PERCENT) REPEATABLE (424242)";
            t.WasSampled = true;
        }
        else
        {
            fromClause = $"FROM {Escape(t.SchemaName)}.{Escape(t.TableName)}";
            t.WasSampled = false;
        }

        using var conn = Open();

        foreach (var batch in Chunk(analyzable, _opt.ColumnBatchSize))
        {
            var select = new System.Text.StringBuilder();
            select.Append("SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;\n");
            select.Append("SELECT COUNT_BIG(*) AS __rows");
            for (var i = 0; i < batch.Count; i++)
            {
                var col = Escape(batch[i].ColumnName);
                select.Append($",\n  COUNT_BIG(DISTINCT {col}) AS d_{i}");
                select.Append($",\n  COUNT_BIG({col}) AS nn_{i}");
                select.Append($",\n  AVG(CAST(DATALENGTH({col}) AS float)) AS len_{i}");
            }

            select.Append('\n').Append(fromClause).Append(" OPTION (MAXDOP 2);");

            using var cmd = new SqlCommand(select.ToString(), conn);
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) continue;

            var rows = r.GetInt64(0);
            t.SampledRows = rows;

            for (var i = 0; i < batch.Count; i++)
            {
                var c = batch[i];
                c.Analyzed = true;
                c.SampleRows = rows;
                c.DistinctValues = r.GetInt64(r.GetOrdinal($"d_{i}"));
                var nonNull = r.GetInt64(r.GetOrdinal($"nn_{i}"));
                c.NullCount = rows - nonNull;
                var lenOrd = r.GetOrdinal($"len_{i}");
                c.AvgByteLength = r.IsDBNull(lenOrd) ? 0 : r.GetDouble(lenOrd);
            }
        }

        // Table-level data-shape aggregates
        var done = t.Columns.Where(c => c is { Analyzed: true, SampleRows: > 0 }).ToList();
        if (done.Count > 0)
        {
            t.PctLowCardinalityColumns = 100.0 * done.Count(c => c.DistinctRatio <= 0.01) / done.Count;
            t.PctMediumCardinalityColumns = 100.0 * done.Count(c => c.DistinctRatio <= 0.10) / done.Count;
            var factors = done.Select(c => c.DuplicationFactor).OrderBy(x => x).ToList();
            t.MedianDuplicationFactor = factors[factors.Count / 2];
            t.DictionaryPressureColumns = done.Count(c => c.DictionaryPressure);
        }

        t.LobColumns = t.Columns.Count(c => c.IsLob);
    }

    /// <summary>One pass over the plan cache; statements matched to tables client-side by name.</summary>
    public void MatchPlanCacheQueries(List<TableInfo> tables)
    {
        var all = new List<CachedQuery>();
        using (var conn = Open())
        using (var cmd = new SqlCommand(Sql.PlanCacheStatements, conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    if (r.IsDBNull(5)) continue;
                    all.Add(new CachedQuery
                    {
                        ExecutionCount = r.GetInt64(0),
                        TotalLogicalReads = r.GetInt64(1),
                        TotalCpuMs = r.GetDouble(2),
                        TotalElapsedMs = r.GetDouble(3),
                        LastExecutionTime = r.GetDateTime(4),
                        StatementText = r.GetString(5)
                    });
                }
            }
        }

        foreach (var t in tables)
        {
            var matches = all
                .Where(q => q.StatementText.Contains(t.TableName, StringComparison.OrdinalIgnoreCase)
                            && !q.StatementText.Contains("AS __rows", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(q => q.TotalLogicalReads)
                .Take(_opt.MaxQueriesPerTable);
            t.ReferencingQueries.AddRange(matches);
        }
    }

    /// <summary>Supplements MatchPlanCacheQueries with Query Store's persisted, restart-surviving
    /// query history for the connected database - same table-name-substring matching approach.
    /// Entirely additive: if Query Store is off (the common case for anyone not using it), this is a
    /// no-op and the plan-cache evidence above stands alone, unchanged from before this existed.
    /// Returns a caveat string when Query Store is enabled but not actually trustworthy right now
    /// (stuck READ_ONLY - the well-known "silently stopped capturing data" gotcha); null otherwise.</summary>
    public string? MatchQueryStoreQueries(List<TableInfo> tables)
    {
        using var conn = Open();

        string actualState;
        int readonlyReason;
        long currentMb, maxMb;
        using (var cmd = new SqlCommand(Sql.QueryStoreStatus, conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null; // shouldn't happen on 2016+, but nothing to add either way
            actualState = r.GetString(0);
            readonlyReason = r.GetInt32(1);
            currentMb = r.GetInt64(2);
            maxMb = r.GetInt64(3);
        }

        if (actualState == "OFF") return null;

        // Bits per sys.database_query_store_options.readonly_reason: 65536=hit max_storage_size_mb,
        // 131072=too many distinct statements, 524288=database itself out of disk space - all mean
        // Query Store has silently stopped capturing new data, with no alert of its own.
        if (actualState == "READ_ONLY" && (readonlyReason & (65536 | 131072 | 524288)) != 0)
            return $"Query Store is stuck READ_ONLY (using {currentMb:N0} of {maxMb:N0} MB allocated) - " +
                   "it has stopped capturing new query data, so the referencing-queries evidence above may be stale.";

        var all = new List<CachedQuery>();
        using (var cmd = new SqlCommand(Sql.QueryStoreTopQueries, conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            cmd.Parameters.AddWithValue("@N", 500); // headroom for per-table substring matching below
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                all.Add(new CachedQuery
                {
                    Source = "QueryStore",
                    StatementText = r.GetString(0),
                    ExecutionCount = r.GetInt64(1),
                    TotalLogicalReads = (long)r.GetDouble(2),
                    TotalCpuMs = r.GetDouble(3),
                    TotalElapsedMs = r.GetDouble(4),
                    LastExecutionTime = r.GetDateTime(5)
                });
            }
        }

        foreach (var t in tables)
        {
            var matches = all
                .Where(q => q.StatementText.Contains(t.TableName, StringComparison.OrdinalIgnoreCase)
                            && !q.StatementText.Contains("AS __rows", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(q => q.TotalLogicalReads)
                .Take(_opt.MaxQueriesPerTable);
            t.ReferencingQueries.AddRange(matches);
        }

        return null;
    }

    internal static string Escape(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }
}