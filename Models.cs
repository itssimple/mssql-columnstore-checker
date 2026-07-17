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
    public int ObjectId { get; set; }
    public string SchemaName { get; set; } = "";
    public string TableName { get; set; } = "";
    public string FullName => $"[{SchemaName}].[{TableName}]";
    public long RowCount { get; set; }
    public decimal TotalSizeMb { get; set; }
    public decimal BaseDataMb { get; set; }
    public decimal NonclusteredMb { get; set; }
    public bool HasColumnstore { get; set; }
    public bool IsReplicated { get; set; }
    public bool IsCdcTracked { get; set; }

    // Workload (DMV) profile
    public long UserSeeks { get; set; }
    public long UserScans { get; set; }
    public long UserLookups { get; set; }
    public long UserUpdates { get; set; }
    public long RowLockWaitMs { get; set; }
    public long PageLockWaitMs { get; set; }
    public long PageIoLatchWaitMs { get; set; }
    public long LeafInserts { get; set; }
    public long LeafUpdates { get; set; }
    public long LeafDeletes { get; set; }
    public int MissingIndexSuggestions { get; set; }

    public List<ColumnStat> Columns { get; } = new();
    public List<IndexInfo> Indexes { get; } = new();
    public List<CachedQuery> ReferencingQueries { get; } = new();

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
    public string ColumnName { get; set; } = "";
    public string DataType { get; set; } = "";
    public short MaxLength { get; set; }
    public bool IsLob { get; set; }
    public bool IsComputed { get; set; }
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
            if (DuplicationFactor >= 100) return "EXCELLENT - highly repetitive, compresses extremely well";
            if (DuplicationFactor >= 10) return "GOOD - solid run-length/dictionary compression expected";
            if (DictionaryPressure) return "BAD - high-cardinality wide string, dictionary pressure risk";
            if (DistinctRatio > 0.9) return "POOR - nearly unique values, little compression benefit";
            return "NEUTRAL";
        }
    }
}

public sealed class IndexInfo
{
    public string IndexName { get; set; } = "";
    public string TypeDesc { get; set; } = "";
    public decimal SizeMb { get; set; }
    public int KeyColumnCount { get; set; }
    public int IncludedColumnCount { get; set; }
    public string KeyColumns { get; set; } = "";
    public string IncludedColumns { get; set; } = "";
    public long UserSeeks { get; set; }
    public long UserScans { get; set; }
    public long UserLookups { get; set; }
    public long UserUpdates { get; set; }
    public string Note { get; set; } = "";
}

public sealed class CachedQuery
{
    public long ExecutionCount { get; set; }
    public long TotalLogicalReads { get; set; }
    public double TotalCpuMs { get; set; }
    public double TotalElapsedMs { get; set; }
    public DateTime LastExecutionTime { get; set; }
    public string StatementText { get; set; } = "";
}
