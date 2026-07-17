namespace ColumnstoreAnalyzer;

public sealed class AnalyzerOptions
{
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public string? User { get; set; }
    public string? Password { get; set; }
    /// <summary>Explicitly read the password from stdin (for scripts/CI): --password-stdin</summary>
    public bool PasswordFromStdin { get; set; }
    public bool TrustServerCertificate { get; set; } = true;

    public long MinRowCount { get; set; } = 1_000_000;
    public long MinTableSizeMb { get; set; } = 500;
    public int TopNCandidates { get; set; } = 25;

    /// <summary>Target number of rows to sample per table when measuring column cardinality.</summary>
    public long SampleTargetRows { get; set; } = 1_000_000;

    /// <summary>Max columns per cardinality query (keeps individual queries cheap).</summary>
    public int ColumnBatchSize { get; set; } = 20;

    public int QueryTimeoutSeconds { get; set; } = 600;
    public int MaxQueriesPerTable { get; set; } = 20;

    /// <summary>Optional: OpenAI-compatible endpoint (e.g. http://localhost:11434 for Ollama). Null = rule-based narrative only.</summary>
    public string? LlmEndpoint { get; set; }
    public string LlmModel { get; set; } = "llama3.1";
    public string? LlmApiKey { get; set; }
    public int LlmTimeoutSeconds { get; set; } = 300;
    public string OutputFolder { get; set; } =
        Path.Combine(Environment.CurrentDirectory, $"columnstore_report_{DateTime.Now:yyyyMMdd_HHmmss}");
}

public sealed class TableInfo
{
    public int ObjectId { get; init; }
    public string SchemaName { get; init; } = "";
    public string TableName { get; init; } = "";
    public string FullName => $"[{SchemaName}].[{TableName}]";
    public long RowCount { get; init; }
    public decimal TotalSizeMb { get; init; }
    public decimal BaseDataMb { get; init; }
    public decimal NonclusteredMb { get; init; }
    public bool HasColumnstore { get; init; }
    public bool IsReplicated { get; init; }
    public bool IsCdcTracked { get; init; }

    // Workload (DMV) profile
    public long UserSeeks { get; init; }
    public long UserScans { get; init; }
    public long UserLookups { get; init; }
    public long UserUpdates { get; init; }
    public long RowLockWaitMs { get; set; }
    public long PageLockWaitMs { get; set; }
    public long PageIoLatchWaitMs { get; set; }
    public long LeafInserts { get; set; }
    public long LeafUpdates { get; set; }
    public long LeafDeletes { get; set; }
    public int MissingIndexSuggestions { get; init; }

    public List<ColumnStat> Columns { get; } = [];
    public List<IndexInfo> Indexes { get; } = [];
    public List<CachedQuery> ReferencingQueries { get; } = [];

    // Derived
    public double ScanPct
    {
        get
        {
            var reads = UserSeeks + UserScans + UserLookups;
            return reads == 0 ? 0 : 100.0 * UserScans / reads;
        }
    }

    public double WritePct
    {
        get
        {
            var all = UserSeeks + UserScans + UserLookups + UserUpdates;
            return all == 0 ? 0 : 100.0 * UserUpdates / all;
        }
    }

    public double UpdateShareOfWritesPct
    {
        get
        {
            var writes = LeafInserts + LeafUpdates + LeafDeletes;
            return writes == 0 ? 0 : 100.0 * LeafUpdates / writes;
        }
    }

    public double NcIndexBloatRatio => BaseDataMb == 0 ? 0 : (double)(NonclusteredMb / BaseDataMb);

    public double PotentialFullRowgroups => RowCount / 1_048_576.0;

    public double CandidacyScore { get; set; }
    public string AssessmentNotes { get; set; } = "";

    // Data-shape aggregates (filled after column analysis)
    public double PctLowCardinalityColumns { get; set; }     // distinct ratio <= 1%
    public double PctMediumCardinalityColumns { get; set; }  // distinct ratio <= 10%
    public double MedianDuplicationFactor { get; set; }
    public int DictionaryPressureColumns { get; set; }
    public int LobColumns { get; set; }
    public long SampledRows { get; set; }
    public bool WasSampled { get; set; }
}

public sealed class ColumnStat
{
    public string ColumnName { get; init; } = "";
    public string DataType { get; init; } = "";
    public short MaxLength { get; init; }
    public bool IsLob { get; init; }
    public bool IsComputed { get; init; }
    public bool Analyzed { get; set; }
    public string SkipReason { get; set; } = "";

    public long SampleRows { get; set; }
    public long DistinctValues { get; set; }
    public long NullCount { get; set; }
    public double AvgByteLength { get; set; }

    /// <summary>distinct / non-null rows. Low = repetitive = compresses beautifully.</summary>
    public double DistinctRatio
    {
        get
        {
            var nonNull = SampleRows - NullCount;
            return nonNull <= 0 ? 0 : (double)DistinctValues / nonNull;
        }
    }

    /// <summary>How many times each value repeats on average. High = columnstore gold.</summary>
    public double DuplicationFactor
    {
        get
        {
            var nonNull = SampleRows - NullCount;
            return DistinctValues == 0 ? 0 : (double)nonNull / DistinctValues;
        }
    }

    public bool IsStringType =>
        DataType is "varchar" or "nvarchar" or "char" or "nchar";

    /// <summary>
    /// High-cardinality wide strings blow up columnstore dictionaries (64KB per-segment /
    /// 16MB global limits), causing trimmed rowgroups and poor compression.
    /// </summary>
    public bool DictionaryPressure =>
        IsStringType && Analyzed && DistinctRatio > 0.5 && AvgByteLength > 16;

    public string Verdict
    {
        get
        {
            if (!Analyzed) return SkipReason;
            switch (DuplicationFactor)
            {
                case >= 100:
                    return "EXCELLENT - highly repetitive, compresses extremely well";
                case >= 10:
                    return "GOOD - solid run-length/dictionary compression expected";
            }

            if (DictionaryPressure) return "BAD - high-cardinality wide string, dictionary pressure risk";
            
            return DistinctRatio > 0.9 ? "POOR - nearly unique values, little compression benefit" : "NEUTRAL";
        }
    }
}

public sealed class IndexInfo
{
    public string IndexName { get; init; } = "";
    public string TypeDesc { get; init; } = "";
    public decimal SizeMb { get; init; }
    public int KeyColumnCount { get; init; }
    public int IncludedColumnCount { get; init; }
    public string KeyColumns { get; init; } = "";
    public string IncludedColumns { get; init; } = "";
    public long UserSeeks { get; init; }
    public long UserScans { get; init; }
    public long UserLookups { get; init; }
    public long UserUpdates { get; init; }
    public string Note { get; set; } = "";
}

public sealed class CachedQuery
{
    public long ExecutionCount { get; init; }
    public long TotalLogicalReads { get; init; }
    public double TotalCpuMs { get; init; }
    public double TotalElapsedMs { get; init; }
    public DateTime LastExecutionTime { get; init; }
    public string StatementText { get; init; } = "";
}
