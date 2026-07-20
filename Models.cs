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

    // ---- Health check (opt-in; adds master/msdb reads + EXEC on any installed FRK procs) ----
    public bool RunHealthCheck { get; set; }
    public bool SkipFrk { get; set; }
    public bool SkipOla { get; set; }
    public bool SkipWhoIsActive { get; set; }
    public bool IncludeBlitzLock { get; set; }
    public bool IncludeBlitzBackups { get; set; }
    public string ToolsDatabase { get; set; } = "master";
    /// <summary>Extra live @Seconds= wait-stat sample captured alongside the always-run instant sp_BlitzFirst
    /// snapshot. Defaults to 30s (adds that much real wall-clock time to --health-check runs); 0 skips the extra sample.</summary>
    public int BlitzFirstSampleSeconds { get; set; } = 30;
    public bool HealthCheckAllDatabases { get; set; }
    /// <summary>Opt-in escalation: allows CREATE PROCEDURE/agent job creation for missing FRK/Ola components. Never on by default.</summary>
    public bool InstallMissingTools { get; set; }
    public bool WriteHtmlReport { get; set; } = true;

    /// <summary>Standalone mode: instance-wide logins/permissions inventory across every database.
    /// Runs instead of (not alongside) the columnstore pipeline - --database is not required.</summary>
    public bool PermissionsReport { get; set; }

    /// <summary>Standalone, strictly read-only mode: exercises every code path (a small sample of
    /// columnstore tables, every health-check native check, FRK detection + instant EXEC if
    /// installed, Ola detection, the permissions inventory) and writes a pass/fail diagnostic report
    /// instead of the normal reports. Never touches the install-flow - refuses to run combined with
    /// --install-missing-tools. Meant to be run manually against a real instance and the resulting
    /// file handed back for review, since this tool has no live database access of its own.</summary>
    public bool SelfTest { get; set; }
}

public enum HealthCheckSeverity { Info, Low, Medium, High, Critical }

public sealed class HealthCheckFinding
{
    public string Source { get; init; } = "";     // "Native", "sp_Blitz", "sp_BlitzIndex", "Ola Hallengren", ...
    public string Category { get; init; } = "";    // "Backups", "Configuration", "Security", "Maintenance", ...
    public HealthCheckSeverity Severity { get; init; } = HealthCheckSeverity.Info;
    public string Title { get; init; } = "";
    public string Details { get; init; } = "";
    public string? Recommendation { get; init; }
    public string? DatabaseName { get; init; }
    public string? ObjectName { get; init; }
    public Dictionary<string, string?> RawColumns { get; init; } = [];

    /// <summary>True when this finding is a gap only the departing owner can fill in (exit-interview capture list).</summary>
    public bool NeedsAnnotation { get; init; }
    public string? AnswerPlaceholder { get; init; }
}

public sealed class ComponentStatus
{
    public string Name { get; init; } = "";        // "sp_Blitz", "Ola Hallengren Maintenance Solution", "sp_WhoIsActive"
    public bool Installed { get; init; }
    public string? Version { get; init; }
    public string InstallInstructions { get; init; } = "";
}

public sealed class MaintenanceJobStatus
{
    public string JobNamePattern { get; init; } = "";  // e.g. "DatabaseBackup - %"
    public string? JobName { get; init; }
    public bool Found { get; init; }
    public bool Enabled { get; init; }
    public DateTime? LastRunTime { get; init; }
    public string? LastRunOutcome { get; init; }       // Succeeded/Failed/Retry/Canceled
    public DateTime? NextRunTime { get; init; }
}

/// <summary>Generic capture of an EXEC'd stored proc's result set, whose schema isn't known in advance.</summary>
public sealed class DynamicResultSet
{
    public string Name { get; init; } = "";
    public List<string> ColumnNames { get; init; } = [];
    public List<Dictionary<string, object?>> Rows { get; init; } = [];
}

public sealed class InstallAction
{
    public string Component { get; init; } = "";
    public string PinnedVersion { get; init; } = "";
    public DateTime When { get; init; }
    public bool Succeeded { get; init; }
    public string Detail { get; init; } = "";
}

// ==========================================================================================
// --permissions-report: standalone instance-wide logins/permissions inventory
// ==========================================================================================

public sealed class ServerPrincipalInfo
{
    public string Name { get; init; } = "";
    public string TypeDesc { get; init; } = "";   // SQL_LOGIN, WINDOWS_LOGIN, WINDOWS_GROUP
    public bool IsDisabled { get; init; }
    public DateTime CreateDate { get; init; }
    public string? DefaultDatabaseName { get; init; }
    public List<string> ServerRoles { get; init; } = [];

    /// <summary>How many database users (across every database scanned) map to this login - the
    /// "is this disabled login actually still lingering with live access" signal.</summary>
    public int DatabaseUserCount { get; set; }
}

public sealed class DatabaseUserInfo
{
    public string DatabaseName { get; init; } = "";
    public string UserName { get; init; } = "";
    public string TypeDesc { get; init; } = "";   // SQL_USER, WINDOWS_USER, WINDOWS_GROUP
    public string? LoginName { get; init; }       // null if orphaned or a login-less contained-DB user
    public bool IsOrphaned { get; init; }
    public List<string> DatabaseRoles { get; init; } = [];
}

/// <summary>One explicit (not fixed-role-implicit) GRANT/DENY at database, schema, or object scope.</summary>
public sealed class ObjectPermissionInfo
{
    public string DatabaseName { get; init; } = "";
    public string GranteeName { get; init; } = "";
    public string GranteeType { get; init; } = ""; // e.g. SQL_USER, DATABASE_ROLE, APPLICATION_ROLE
    public string ClassDesc { get; init; } = "";   // DATABASE, OBJECT_OR_COLUMN, SCHEMA
    public string PermissionName { get; init; } = "";
    public string StateDesc { get; init; } = "";   // GRANT, DENY, GRANT_WITH_GRANT_OPTION
    public string? SchemaName { get; init; }
    public string? ObjectName { get; init; }
    /// <summary>Set only for column-level grants (ClassDesc=OBJECT_OR_COLUMN with a nonzero minor_id) -
    /// null for whole-object, schema, or database-level grants.</summary>
    public string? ColumnName { get; init; }
}

public sealed class PermissionsReportResult
{
    public string ServerName { get; init; } = "";
    public DateTime GeneratedAt { get; init; }
    public List<ServerPrincipalInfo> ServerPrincipals { get; } = [];
    public List<DatabaseUserInfo> DatabaseUsers { get; } = [];
    public List<ObjectPermissionInfo> ObjectPermissions { get; } = [];
    /// <summary>Computed risk findings (sysadmin inventory, disabled-but-still-mapped logins, orphaned
    /// users, risky object grants) - reuses HealthCheckFinding/HealthCheckSeverity rather than a
    /// parallel type, since the shape (Source/Category/Severity/Title/Details/DatabaseName) already fits.</summary>
    public List<HealthCheckFinding> Findings { get; } = [];
    /// <summary>Databases skipped due to errors (typically insufficient permission) - non-fatal by design.</summary>
    public List<string> Warnings { get; } = [];
}

public sealed class HealthCheckResult
{
    public List<ComponentStatus> Components { get; } = [];
    public List<MaintenanceJobStatus> MaintenanceJobs { get; } = [];
    public List<HealthCheckFinding> Findings { get; } = [];
    public List<DynamicResultSet> RawResultSets { get; } = [];
    public List<InstallAction> InstallActions { get; } = [];
    /// <summary>Persisted, restart-surviving query performance history - only populated for
    /// databases where Query Store is actually capturing data (state != OFF).</summary>
    public List<QueryStoreTopQuery> QueryStoreTopQueries { get; } = [];
    /// <summary>Empty on an instance with no Availability Group - both DMVs behind this return zero
    /// rows gracefully in that case, not an error.</summary>
    public List<AvailabilityReplicaStatus> AvailabilityReplicas { get; } = [];
    public List<AvailabilityDatabaseStatus> AvailabilityDatabases { get; } = [];
}

/// <summary>Query Store's config/state for one database. A separate Info/Low/High HealthCheckFinding
/// is generated per database from this (see HealthCheckAnalyzer.RunQueryStoreStatus) - this type just
/// carries the raw numbers.</summary>
public sealed class QueryStoreStatus
{
    public string DatabaseName { get; init; } = "";
    public string ActualStateDesc { get; init; } = "";   // OFF, READ_ONLY, READ_WRITE, ERROR
    public string DesiredStateDesc { get; init; } = "";
    /// <summary>Bitmask - see sys.database_query_store_options docs. Notably: 65536 = hit
    /// max_storage_size_mb, 131072 = too many distinct statements, 524288 = database out of disk space.</summary>
    public int ReadonlyReason { get; init; }
    public decimal CurrentStorageSizeMb { get; init; }
    public decimal MaxStorageSizeMb { get; init; }
    public string QueryCaptureModeDesc { get; init; } = "";
}

public sealed class QueryStoreTopQuery
{
    public string DatabaseName { get; init; } = "";
    public long QueryId { get; init; }
    public string QueryText { get; init; } = "";
    public long TotalExecutions { get; init; }
    public double AvgCpuTimeMs { get; init; }
    public double AvgDurationMs { get; init; }
    /// <summary>8KB pages - same unit as CachedQuery.TotalLogicalReads elsewhere in this tool.</summary>
    public double AvgLogicalReads { get; init; }
}

/// <summary>One Availability Group replica's connection/sync health (server-wide, not per-database).</summary>
public sealed class AvailabilityReplicaStatus
{
    public string AgName { get; init; } = "";
    public string ReplicaServerName { get; init; } = "";
    public string RoleDesc { get; init; } = "";                   // PRIMARY, SECONDARY
    public string ConnectedStateDesc { get; init; } = "";         // CONNECTED, DISCONNECTED
    public string SynchronizationHealthDesc { get; init; } = "";  // HEALTHY, PARTIALLY_HEALTHY, NOT_HEALTHY
}

/// <summary>One database's replication state on one AG replica - the actual lag/queue-depth signal.</summary>
public sealed class AvailabilityDatabaseStatus
{
    public string AgName { get; init; } = "";
    public string ReplicaServerName { get; init; } = "";
    public string DatabaseName { get; init; } = "";
    public bool IsSuspended { get; init; }
    public string SynchronizationStateDesc { get; init; } = "";  // SYNCHRONIZED, SYNCHRONIZING, NOT_SYNCHRONIZING
    public long LogSendQueueSizeKb { get; init; }
    public long RedoQueueSizeKb { get; init; }
    /// <summary>Null on the primary (only meaningful for secondaries).</summary>
    public double? SecondaryLagSeconds { get; init; }
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

// ==========================================================================================
// --self-test: read-only exercise of every code path, for a human to run manually against a
// real instance (this tool never gets live DB access) and hand the resulting file back.
// ==========================================================================================

public sealed class SelfTestStepResult
{
    public string Category { get; init; } = "";  // Columnstore, HealthCheck-Native, HealthCheck-FRK, Permissions
    public string Step { get; init; } = "";
    public bool Success { get; init; }
    public long ElapsedMs { get; init; }
    /// <summary>Row/column counts and shape only - deliberately never actual data values, since this
    /// is meant to be safe to run (and share) against a real production instance.</summary>
    public string? Summary { get; init; }
    public string? ErrorType { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class SelfTestResult
{
    public string ServerName { get; init; } = "";
    public DateTime GeneratedAt { get; init; }
    public List<SelfTestStepResult> Steps { get; } = [];
    public int PassCount => Steps.Count(s => s.Success);
    public int FailCount => Steps.Count(s => !s.Success);
}
