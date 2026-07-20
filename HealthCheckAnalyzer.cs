using Microsoft.Data.SqlClient;

namespace ColumnstoreAnalyzer;

/// <summary>
/// Second (and only other) DB-touching class, same shape as Analyzer.cs: private Open(),
/// one method per check, each opens its own connection and does not itself catch
/// exceptions - that's HealthCheckRunner's job, matching the existing convention where
/// per-step failures are non-fatal but individual Analyzer methods are not defensive.
/// </summary>
public sealed class HealthCheckAnalyzer
{
    private readonly AnalyzerOptions _opt;

    public HealthCheckAnalyzer(AnalyzerOptions opt) => _opt = opt;

    private SqlConnection Open() => ConnectionFactory.Open(_opt);

    // ======================================================================================
    // Tool / maintenance-solution detection
    // ======================================================================================

    private static readonly (string Name, string Url)[] KnownTools =
    [
        ("sp_Blitz", "https://github.com/BrentOzarULTD/SQL-Server-First-Responder-Kit"),
        ("sp_BlitzIndex", "https://github.com/BrentOzarULTD/SQL-Server-First-Responder-Kit"),
        ("sp_BlitzCache", "https://github.com/BrentOzarULTD/SQL-Server-First-Responder-Kit"),
        ("sp_BlitzFirst", "https://github.com/BrentOzarULTD/SQL-Server-First-Responder-Kit"),
        ("sp_BlitzLock", "https://github.com/BrentOzarULTD/SQL-Server-First-Responder-Kit"),
        ("sp_BlitzBackups", "https://github.com/BrentOzarULTD/SQL-Server-First-Responder-Kit"),
        ("sp_WhoIsActive", "https://github.com/amachanic/sp_whoisactive")
    ];

    public List<ComponentStatus> DetectTools()
    {
        var found = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
        using var conn = Open();
        using var cmd = new SqlCommand(string.Format(SqlHealthCheck.DetectToolsFmt, Analyzer.Escape(_opt.ToolsDatabase)), conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            found[r.GetString(0)] = r.IsDBNull(2) ? null : r.GetDateTime(2);

        return KnownTools.Select(k => new ComponentStatus
        {
            Name = k.Name,
            Installed = found.ContainsKey(k.Name),
            Version = found.TryGetValue(k.Name, out var modified) && modified.HasValue
                ? modified.Value.ToString("yyyy-MM-dd") : null,
            InstallInstructions = found.ContainsKey(k.Name)
                ? "" : $"Not found in {_opt.ToolsDatabase} - install from {k.Url}"
        }).ToList();
    }

    public List<MaintenanceJobStatus> DetectOlaHallengren()
    {
        var patterns = new[] { "DatabaseBackup - %", "DatabaseIntegrityCheck - %", "IndexOptimize - %" };
        var jobs = new List<(Guid Id, string Name, bool Enabled)>();

        using (var conn = Open())
        using (var cmd = new SqlCommand(SqlHealthCheck.DetectOlaJobs, conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                jobs.Add((r.GetGuid(0), r.GetString(1), r.GetBoolean(2)));
        }

        var results = new List<MaintenanceJobStatus>();
        foreach (var pattern in patterns)
        {
            var prefix = pattern[..^1]; // "DatabaseBackup - %" -> "DatabaseBackup - "
            var matches = jobs.Where(j => j.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                results.Add(new MaintenanceJobStatus { JobNamePattern = pattern, Found = false });
                continue;
            }

            foreach (var job in matches)
                results.Add(BuildJobStatus(pattern, job.Id, job.Name, job.Enabled));
        }

        return results;
    }

    private MaintenanceJobStatus BuildJobStatus(string pattern, Guid jobId, string jobName, bool enabled)
    {
        string? outcome = null;
        DateTime? lastRun = null, nextRun = null;

        using (var conn = Open())
        using (var cmd = new SqlCommand(SqlHealthCheck.OlaJobLastRun, conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            cmd.Parameters.AddWithValue("@JobId", jobId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                outcome = r.GetString(0);
                lastRun = r.IsDBNull(1) ? null : r.GetDateTime(1);
            }
        }

        using (var conn = Open())
        using (var cmd = new SqlCommand(SqlHealthCheck.OlaJobNextRun, conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            cmd.Parameters.AddWithValue("@JobId", jobId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
                nextRun = r.IsDBNull(0) ? null : r.GetDateTime(0);
        }

        return new MaintenanceJobStatus
        {
            JobNamePattern = pattern, JobName = jobName, Found = true, Enabled = enabled,
            LastRunOutcome = outcome, LastRunTime = lastRun, NextRunTime = nextRun
        };
    }

    // ======================================================================================
    // Native checks (zero external dependency, same read-only DMV pattern as Analyzer.cs)
    // ======================================================================================

    private List<string> GetTargetDatabases()
    {
        if (!_opt.HealthCheckAllDatabases) return [ResolveScopeDatabase()];

        var names = new List<string>();
        using var conn = Open();
        using var cmd = new SqlCommand("SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 ORDER BY name;", conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        using var r = cmd.ExecuteReader();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    private string? _resolvedScopeDatabase;

    /// <summary>_opt.Database is blank in modes that don't require it (--self-test, --permissions-report
    /// doesn't use this at all). Falls back to whatever database the connection actually landed on
    /// (its login's default) rather than passing an empty string into 3-part names/@DatabaseName
    /// parameters - Analyzer.Escape("") produces the literal identifier "[]", which SQL Server rejects
    /// outright (error 8155), and some FRK procs (sp_BlitzCache) validate @DatabaseName and reject ""
    /// with their own error. Cached per HealthCheckAnalyzer instance since it never changes mid-run.</summary>
    private string ResolveScopeDatabase()
    {
        if (!string.IsNullOrWhiteSpace(_opt.Database)) return _opt.Database;
        if (_resolvedScopeDatabase != null) return _resolvedScopeDatabase;

        using var conn = Open();
        using var cmd = new SqlCommand("SELECT DB_NAME();", conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        _resolvedScopeDatabase = (string)cmd.ExecuteScalar()!;
        return _resolvedScopeDatabase;
    }

    public List<HealthCheckFinding> RunWaitStats(int topN = 10)
    {
        var findings = new List<HealthCheckFinding>();
        using var conn = Open();
        using var cmd = new SqlCommand(SqlHealthCheck.WaitStatsTopN, conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        cmd.Parameters.AddWithValue("@N", topN);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var waitType = r.GetString(0);
            findings.Add(new HealthCheckFinding
            {
                Source = "Native", Category = "Performance", Severity = HealthCheckSeverity.Info,
                Title = $"Top wait: {waitType}",
                Details = $"{r.GetInt64(1):N0} ms cumulative wait, {r.GetInt64(2):N0} waiting tasks, " +
                          $"{r.GetInt64(3):N0} ms signal wait (since last restart)."
            });
        }
        return findings;
    }

    public List<HealthCheckFinding> RunCheckdbAge()
    {
        var findings = new List<HealthCheckFinding>();
        foreach (var db in GetTargetDatabases())
        {
            using var conn = Open();
            var sql = string.Format(SqlHealthCheck.CheckdbLastGoodFmt, db.Replace("'", "''"));
            using var cmd = new SqlCommand(sql, conn);
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();

            DateTime? lastGood = null;
            var fieldOrd = -1;
            var valueOrd = -1;
            while (r.Read())
            {
                if (fieldOrd < 0) { fieldOrd = r.GetOrdinal("Field"); valueOrd = r.GetOrdinal("Value"); }
                var field = r.GetValue(fieldOrd)?.ToString()?.Trim() ?? "";
                if (!field.Equals("dbi_dbccLastKnownGood", StringComparison.OrdinalIgnoreCase)) continue;

                var val = r.GetValue(valueOrd);
                if (val is DateTime dt) lastGood = dt;
                else if (DateTime.TryParse(val?.ToString(), out var parsed)) lastGood = parsed;
            }

            if (lastGood is null || lastGood.Value.Year <= 1900)
            {
                findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Integrity", Severity = HealthCheckSeverity.High,
                    Title = $"{db}: no successful CHECKDB on record",
                    Details = "DBCC DBINFO reports no known-good integrity check - corruption could be silently accumulating.",
                    Recommendation = "Run DBCC CHECKDB and schedule it regularly (Ola Hallengren's DatabaseIntegrityCheck job covers this).",
                    DatabaseName = db
                });
            }
            else
            {
                var ageDays = (DateTime.Now - lastGood.Value).TotalDays;
                var severity = ageDays switch { > 30 => HealthCheckSeverity.High, > 14 => HealthCheckSeverity.Medium, _ => HealthCheckSeverity.Info };
                findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Integrity", Severity = severity,
                    Title = $"{db}: last known-good CHECKDB {ageDays:0} day(s) ago",
                    Details = $"Last known-good integrity check: {lastGood:yyyy-MM-dd}.",
                    DatabaseName = db
                });
            }
        }
        return findings;
    }

    public List<HealthCheckFinding> RunBackupRecency()
    {
        var findings = new List<HealthCheckFinding>();
        HashSet<string>? targets = _opt.HealthCheckAllDatabases
            ? null
            : new HashSet<string>(GetTargetDatabases(), StringComparer.OrdinalIgnoreCase);

        using var conn = Open();
        using var cmd = new SqlCommand(SqlHealthCheck.BackupRecency, conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var dbName = r.GetString(0);
            if (targets != null && !targets.Contains(dbName)) continue;

            var recoveryModel = r.GetString(1);
            DateTime? full = r.IsDBNull(2) ? null : r.GetDateTime(2);
            DateTime? log = r.IsDBNull(4) ? null : r.GetDateTime(4);

            if (full is null)
            {
                findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Backups", Severity = HealthCheckSeverity.Critical,
                    Title = $"{dbName}: no full backup on record",
                    Details = "msdb.dbo.backupset has no full-backup history for this database.",
                    Recommendation = "Verify backups are actually running and msdb backup history isn't being purged too aggressively.",
                    DatabaseName = dbName
                });
                continue;
            }

            var fullAgeDays = (DateTime.Now - full.Value).TotalDays;
            if (fullAgeDays > 7)
                findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Backups",
                    Severity = fullAgeDays > 30 ? HealthCheckSeverity.Critical : HealthCheckSeverity.High,
                    Title = $"{dbName}: full backup is {fullAgeDays:0} day(s) old",
                    Details = $"Last full backup: {full:yyyy-MM-dd}.",
                    DatabaseName = dbName
                });

            if (recoveryModel == "FULL" && (log is null || (DateTime.Now - log.Value).TotalHours > 24))
                findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Backups", Severity = HealthCheckSeverity.Critical,
                    Title = $"{dbName}: FULL recovery model with no recent log backup",
                    Details = log is null ? "No log backup on record at all." : $"Last log backup: {log:yyyy-MM-dd HH:mm}.",
                    Recommendation = "Silent point-in-time-recovery gap: the log won't truncate and RPO is effectively 'whenever the last full/diff ran'.",
                    DatabaseName = dbName
                });
        }
        return findings;
    }

    public List<HealthCheckFinding> RunTempdbConfig()
    {
        var findings = new List<HealthCheckFinding>();
        var rows = new List<(bool IsPercentGrowth, int CpuCount, int DataFileCount)>();

        using var conn = Open();
        using var cmd = new SqlCommand(SqlHealthCheck.TempdbConfig, conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add((r.GetBoolean(4), r.GetInt32(5), r.GetInt32(6)));

        if (rows.Count == 0) return findings;
        var cpuCount = rows[0].CpuCount;
        var dataFiles = rows[0].DataFileCount;
        var recommended = Math.Min(cpuCount, 8);

        if (dataFiles < recommended)
            findings.Add(new HealthCheckFinding
            {
                Source = "Native", Category = "Configuration", Severity = HealthCheckSeverity.Medium,
                Title = $"tempdb has {dataFiles} data file(s) vs {cpuCount} CPU(s)",
                Details = $"Community guidance recommends up to one tempdb data file per core (capped at 8): {recommended} recommended, {dataFiles} configured.",
                Recommendation = "Add tempdb data files, all equally sized, to reduce allocation-page (PFS/GAM/SGAM) contention."
            });

        if (rows.Any(x => x.IsPercentGrowth))
            findings.Add(new HealthCheckFinding
            {
                Source = "Native", Category = "Configuration", Severity = HealthCheckSeverity.Low,
                Title = "tempdb has percentage-based autogrowth",
                Details = "Percent growth causes increasingly large, slow autogrow events as the file gets bigger.",
                Recommendation = "Switch to a fixed-MB growth increment."
            });

        return findings;
    }

    public List<HealthCheckFinding> RunConfigSmells()
    {
        var findings = new List<HealthCheckFinding>();
        var config = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        using (var conn = Open())
        using (var cmd = new SqlCommand(SqlHealthCheck.ServerConfigSmells, conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                config[r.GetString(0)] = r.GetInt64(2); // value_in_use
        }

        if (config.TryGetValue("cost threshold for parallelism", out var cost) && cost <= 5)
            findings.Add(new HealthCheckFinding
            {
                Source = "Native", Category = "Configuration", Severity = HealthCheckSeverity.Low,
                Title = "Cost threshold for parallelism left at default",
                Details = $"Currently {cost} (default is 5).",
                Recommendation = "Most guidance suggests raising this to 25-50+ on modern hardware to avoid parallelizing cheap queries."
            });

        if (config.TryGetValue("max degree of parallelism", out var maxdop) && maxdop == 0)
            findings.Add(new HealthCheckFinding
            {
                Source = "Native", Category = "Configuration", Severity = HealthCheckSeverity.Medium,
                Title = "MAXDOP left at default (0 = unlimited)",
                Details = "Unlimited MAXDOP can cause excessive parallelism/CXPACKET waits on multi-socket or high-core-count servers.",
                Recommendation = "Set per Microsoft's core-count-based MAXDOP guidance."
            });

        if (config.TryGetValue("max server memory (MB)", out var maxMem) && maxMem >= int.MaxValue)
            findings.Add(new HealthCheckFinding
            {
                Source = "Native", Category = "Configuration", Severity = HealthCheckSeverity.High,
                Title = "Max server memory not configured",
                Details = "SQL Server can consume effectively all host memory, starving the OS and other processes.",
                Recommendation = "Set 'max server memory (MB)' explicitly, leaving headroom for the OS and any other services on the box."
            });

        using (var conn = Open())
        using (var cmd = new SqlCommand(SqlHealthCheck.DatabaseFlagSmells, conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = r.GetString(0);
                if (r.GetBoolean(1))
                    findings.Add(new HealthCheckFinding
                    {
                        Source = "Native", Category = "Configuration", Severity = HealthCheckSeverity.Medium,
                        Title = $"{name}: auto-shrink is ON",
                        Details = "Auto-shrink runs on an unpredictable schedule, causes heavy fragmentation, and fights with any index-maintenance job.",
                        Recommendation = "Disable auto-shrink; shrink deliberately and rarely if ever needed.",
                        DatabaseName = name
                    });
                if (r.GetBoolean(2))
                    findings.Add(new HealthCheckFinding
                    {
                        Source = "Native", Category = "Configuration", Severity = HealthCheckSeverity.Low,
                        Title = $"{name}: auto-close is ON",
                        Details = "Auto-close tears down the connection pool and plan cache for this database between uses.",
                        DatabaseName = name
                    });
            }
        }

        return findings;
    }

    public List<HealthCheckFinding> RunAgentJobFailures(int days = 7)
    {
        var findings = new List<HealthCheckFinding>();
        using var conn = Open();
        using var cmd = new SqlCommand(SqlHealthCheck.AgentJobFailures, conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        cmd.Parameters.AddWithValue("@Days", days);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var job = r.GetString(0);
            var runTime = r.IsDBNull(1) ? (DateTime?)null : r.GetDateTime(1);
            var message = r.IsDBNull(2) ? "" : r.GetString(2);
            findings.Add(new HealthCheckFinding
            {
                Source = "Native", Category = "Maintenance", Severity = HealthCheckSeverity.Medium,
                Title = $"Agent job failed: {job}",
                Details = $"Failed at {runTime:yyyy-MM-dd HH:mm}: {Trim(message, 300)}"
            });
        }
        return findings;
    }

    // ======================================================================================
    // Tribal-knowledge inventory
    // ======================================================================================

    public List<HealthCheckFinding> RunJobOwnerAudit()
    {
        var findings = new List<HealthCheckFinding>();
        using var conn = Open();
        using var cmd = new SqlCommand(SqlHealthCheck.JobOwnerAudit, conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var job = r.GetString(0);
            var enabled = r.GetBoolean(1);
            var owner = r.GetString(2);
            var ownerType = r.GetString(3);

            var looksPersonal = ownerType is "SQL_LOGIN" or "WINDOWS_LOGIN"
                && !owner.Equals("sa", StringComparison.OrdinalIgnoreCase)
                && !owner.EndsWith('$')
                && !owner.Contains("svc", StringComparison.OrdinalIgnoreCase)
                && !owner.Contains("service", StringComparison.OrdinalIgnoreCase)
                && !owner.StartsWith("NT ", StringComparison.OrdinalIgnoreCase);

            if (!looksPersonal) continue;

            findings.Add(new HealthCheckFinding
            {
                Source = "Native", Category = "Landmine", Severity = HealthCheckSeverity.High,
                Title = $"Job \"{job}\" is owned by what looks like a personal login: {owner}",
                Details = $"Owner type: {ownerType}, job enabled: {enabled}. If that login is disabled after someone leaves, this job's execution context may break.",
                Recommendation = "Re-point ownership to a service account or 'sa', or document why the personal login is required.",
                ObjectName = job, NeedsAnnotation = true,
                AnswerPlaceholder = "Who should own this job instead, or why does it need to stay as-is?"
            });
        }
        return findings;
    }

    public List<HealthCheckFinding> RunSecurityInventory()
    {
        var findings = new List<HealthCheckFinding>();

        using (var conn = Open())
        using (var cmd = new SqlCommand(SqlHealthCheck.SysadminLogins, conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = r.GetString(0);
                var disabled = r.GetBoolean(2);
                findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Security", Severity = HealthCheckSeverity.Info,
                    Title = $"sysadmin member: {name}{(disabled ? " (disabled)" : "")}",
                    Details = $"Login type: {r.GetString(1)}.", ObjectName = name
                });
            }
        }

        using (var conn = Open())
        using (var cmd = new SqlCommand(SqlHealthCheck.DisabledLogins, conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Security", Severity = HealthCheckSeverity.Info,
                    Title = $"Disabled login still present: {r.GetString(0)}",
                    Details = $"Type {r.GetString(1)}, created {r.GetDateTime(2):yyyy-MM-dd}.",
                    Recommendation = "Consider dropping logins that are permanently retired rather than leaving them disabled indefinitely."
                });
        }

        using (var conn = Open())
        using (var cmd = new SqlCommand(SqlHealthCheck.OrphanedUsers, conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Security", Severity = HealthCheckSeverity.Low,
                    Title = $"Orphaned database user: {r.GetString(0)}",
                    Details = "No matching server-level login for this SQL-authenticated database user.",
                    Recommendation = "Drop the user or re-map it with ALTER USER ... WITH LOGIN."
                });
        }

        using (var conn = Open())
        using (var cmd = new SqlCommand(SqlHealthCheck.LinkedServers, conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var name = r.GetString(0);
                findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Landmine", Severity = HealthCheckSeverity.Info,
                    Title = $"Linked server: {name}",
                    Details = $"Product: {r.GetString(1)}, provider: {r.GetString(2)}, data source: {r.GetString(3)}, " +
                              $"uses saved credential: {(r.GetInt32(4) == 1 ? "yes" : "no")}.",
                    ObjectName = name, NeedsAnnotation = true,
                    AnswerPlaceholder = "What depends on this linked server, and is it still needed?"
                });
            }
        }

        return findings;
    }

    public List<HealthCheckFinding> RunTopologyRollup()
    {
        var findings = new List<HealthCheckFinding>();
        using var conn = Open();
        using var cmd = new SqlCommand(SqlHealthCheck.TopologyRollup, conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return findings;

        var replicated = r.GetInt32(0);
        var cdc = r.GetInt32(1);
        // ag_replica_count (column 2) is intentionally unused here - superseded by the real AG health
        // check below (RunAvailabilityGroupHealth), which reports actual sync state, not just a count.

        if (replicated > 0)
            findings.Add(new HealthCheckFinding
            {
                Source = "Native", Category = "Topology", Severity = HealthCheckSeverity.Info,
                Title = $"{replicated} database(s) involved in replication",
                Details = "Published, subscribed, or acting as distributor - check per-database detail before decommissioning anything."
            });
        if (cdc > 0)
            findings.Add(new HealthCheckFinding
            {
                Source = "Native", Category = "Topology", Severity = HealthCheckSeverity.Info,
                Title = $"{cdc} database(s) have Change Data Capture enabled",
                Details = "See per-table CDC flags in the columnstore report for which tables are tracked."
            });

        return findings;
    }

    // ======================================================================================
    // Availability Group / replication health - real sync-state/lag checks, not just counts.
    // ======================================================================================

    public List<AvailabilityReplicaStatus> GetAvailabilityReplicaHealth()
    {
        var results = new List<AvailabilityReplicaStatus>();
        using var conn = Open();
        using var cmd = new SqlCommand(SqlHealthCheck.AvailabilityReplicaHealth, conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            results.Add(new AvailabilityReplicaStatus
            {
                AgName = r.GetString(0), ReplicaServerName = r.GetString(1),
                RoleDesc = r.GetString(2), ConnectedStateDesc = r.GetString(3), SynchronizationHealthDesc = r.GetString(4)
            });
        return results;
    }

    public List<AvailabilityDatabaseStatus> GetAvailabilityDatabaseHealth()
    {
        var results = new List<AvailabilityDatabaseStatus>();
        using var conn = Open();
        using var cmd = new SqlCommand(SqlHealthCheck.AvailabilityDatabaseHealth, conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            results.Add(new AvailabilityDatabaseStatus
            {
                AgName = r.GetString(0), ReplicaServerName = r.GetString(1), DatabaseName = r.GetString(2),
                IsSuspended = r.GetBoolean(3), SynchronizationStateDesc = r.GetString(4),
                LogSendQueueSizeKb = r.IsDBNull(5) ? 0 : r.GetInt64(5), RedoQueueSizeKb = r.IsDBNull(6) ? 0 : r.GetInt64(6),
                SecondaryLagSeconds = r.IsDBNull(7) ? (double?)null : r.GetInt64(7)
            });
        return results;
    }

    /// <summary>One finding per unhealthy replica or database; a single Info summary finding if
    /// everything's fine (avoids silence being ambiguous between "healthy" and "never checked").</summary>
    public List<HealthCheckFinding> RunAvailabilityGroupHealth(out List<AvailabilityReplicaStatus> replicas,
        out List<AvailabilityDatabaseStatus> databases)
    {
        replicas = GetAvailabilityReplicaHealth();
        databases = GetAvailabilityDatabaseHealth();
        var findings = new List<HealthCheckFinding>();

        foreach (var rep in replicas)
        {
            if (rep.ConnectedStateDesc != "CONNECTED")
                findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Availability", Severity = HealthCheckSeverity.High,
                    Title = $"AG '{rep.AgName}': replica {rep.ReplicaServerName} is {rep.ConnectedStateDesc}",
                    Details = $"Role: {rep.RoleDesc}.", Recommendation = "Investigate connectivity between AG replicas immediately."
                });
            else if (rep.SynchronizationHealthDesc == "NOT_HEALTHY")
                findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Availability", Severity = HealthCheckSeverity.High,
                    Title = $"AG '{rep.AgName}': replica {rep.ReplicaServerName} sync health is NOT_HEALTHY",
                    Details = $"Role: {rep.RoleDesc}."
                });
            else if (rep.SynchronizationHealthDesc == "PARTIALLY_HEALTHY")
                findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Availability", Severity = HealthCheckSeverity.Medium,
                    Title = $"AG '{rep.AgName}': replica {rep.ReplicaServerName} sync health is PARTIALLY_HEALTHY",
                    Details = $"Role: {rep.RoleDesc} - at least one database on this replica isn't fully synchronized."
                });
        }

        const long QueueWarningKb = 1_048_576; // 1 GB
        foreach (var db in databases)
        {
            if (db.IsSuspended)
                findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Availability", Severity = HealthCheckSeverity.Critical,
                    Title = $"AG '{db.AgName}': {db.DatabaseName} data movement is SUSPENDED on {db.ReplicaServerName}",
                    Details = "This database has stopped replicating entirely - it is not protected until resumed.",
                    Recommendation = "ALTER DATABASE ... SET HADR RESUME, after investigating why it was suspended.",
                    DatabaseName = db.DatabaseName
                });
            else if (db.SynchronizationStateDesc == "NOT_SYNCHRONIZING")
                findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Availability", Severity = HealthCheckSeverity.High,
                    Title = $"AG '{db.AgName}': {db.DatabaseName} is NOT_SYNCHRONIZING on {db.ReplicaServerName}",
                    DatabaseName = db.DatabaseName
                });

            if (db.SecondaryLagSeconds is > 300)
                findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Availability", Severity = HealthCheckSeverity.Medium,
                    Title = $"AG '{db.AgName}': {db.DatabaseName} on {db.ReplicaServerName} is {db.SecondaryLagSeconds:N0}s behind",
                    Recommendation = "Check network throughput and redo-thread CPU on the secondary if this persists.",
                    DatabaseName = db.DatabaseName
                });

            if (db.RedoQueueSizeKb > QueueWarningKb)
                findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Availability", Severity = HealthCheckSeverity.Medium,
                    Title = $"AG '{db.AgName}': {db.DatabaseName} redo queue on {db.ReplicaServerName} is {db.RedoQueueSizeKb / 1024:N0} MB",
                    Details = "A large, growing redo queue means the secondary can't keep up with incoming log records.",
                    DatabaseName = db.DatabaseName
                });
        }

        if (findings.Count == 0 && replicas.Count > 0)
            findings.Add(new HealthCheckFinding
            {
                Source = "Native", Category = "Availability", Severity = HealthCheckSeverity.Info,
                Title = $"{replicas.Count} AG replica(s) across {replicas.Select(r => r.AgName).Distinct().Count()} Availability Group(s) - all healthy"
            });

        return findings;
    }

    /// <summary>Lighter than the AG check above by design: legacy transactional/snapshot/merge
    /// replication error surfacing only, not a full latency deep-dive. Only runs anything if this
    /// instance is actually configured as a distributor.</summary>
    public List<HealthCheckFinding> RunReplicationErrors()
    {
        var findings = new List<HealthCheckFinding>();
        var distributionDbs = new List<string>();

        using (var conn = Open())
        using (var cmd = new SqlCommand(SqlHealthCheck.DistributionDatabases, conn))
        {
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();
            while (r.Read()) distributionDbs.Add(r.GetString(0));
        }

        foreach (var distDb in distributionDbs)
        {
            using var conn = Open();
            using var cmd = new SqlCommand(string.Format(SqlHealthCheck.ReplicationErrorsFmt, Analyzer.Escape(distDb)), conn);
            cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
            using var r = cmd.ExecuteReader();

            var errors = new List<(DateTime Time, string Text)>();
            while (r.Read())
                errors.Add((r.GetDateTime(0), r.GetString(1)));

            if (errors.Count > 0)
                findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Availability", Severity = HealthCheckSeverity.High,
                    Title = $"{errors.Count} replication error(s) in the last 24h ({distDb})",
                    Details = string.Join(" | ", errors.Take(5).Select(e => $"{e.Time:yyyy-MM-dd HH:mm}: {e.Text}")) +
                              (errors.Count > 5 ? $" ... and {errors.Count - 5} more" : "")
                });
        }

        return findings;
    }

    // ======================================================================================
    // Query Store - persisted, restart-surviving query performance history (unlike plan cache).
    // ======================================================================================

    public QueryStoreStatus GetQueryStoreStatus(string database)
    {
        using var conn = Open();
        using var cmd = new SqlCommand(string.Format(SqlHealthCheck.QueryStoreStatusFmt, Analyzer.Escape(database)), conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            return new QueryStoreStatus { DatabaseName = database, ActualStateDesc = "OFF" };

        return new QueryStoreStatus
        {
            DatabaseName = database,
            ActualStateDesc = r.GetString(0),
            DesiredStateDesc = r.GetString(1),
            ReadonlyReason = r.GetInt32(2),
            CurrentStorageSizeMb = r.GetInt64(3),
            MaxStorageSizeMb = r.GetInt64(4),
            QueryCaptureModeDesc = r.GetString(5)
        };
    }

    public List<QueryStoreTopQuery> GetQueryStoreTopQueries(string database, int topN = 10)
    {
        var results = new List<QueryStoreTopQuery>();
        using var conn = Open();
        using var cmd = new SqlCommand(string.Format(SqlHealthCheck.QueryStoreTopQueriesFmt, Analyzer.Escape(database)), conn);
        cmd.CommandTimeout = _opt.QueryTimeoutSeconds;
        cmd.Parameters.AddWithValue("@N", topN);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            results.Add(new QueryStoreTopQuery
            {
                DatabaseName = database,
                QueryId = r.GetInt64(0),
                QueryText = r.GetString(1),
                TotalExecutions = r.GetInt64(2),
                AvgCpuTimeMs = r.GetDouble(3) / 1000.0,
                AvgDurationMs = r.GetDouble(4) / 1000.0,
                AvgLogicalReads = r.GetDouble(5)
            });
        }
        return results;
    }

    /// <summary>One finding per database in scope: not enabled (Low), stuck READ_ONLY due to a real
    /// problem like hitting its storage cap (High - a well-known gotcha where it silently stops
    /// capturing new data with no alert), or active and healthy (Info).</summary>
    public List<HealthCheckFinding> RunQueryStoreStatus()
    {
        var findings = new List<HealthCheckFinding>();
        foreach (var db in GetTargetDatabases())
            findings.Add(BuildQueryStoreFinding(GetQueryStoreStatus(db)));
        return findings;
    }

    /// <summary>Top queries for every database in scope where Query Store is actually capturing data.</summary>
    public List<QueryStoreTopQuery> RunQueryStoreTopQueries(int topN = 10)
    {
        var results = new List<QueryStoreTopQuery>();
        foreach (var db in GetTargetDatabases())
        {
            if (GetQueryStoreStatus(db).ActualStateDesc == "OFF") continue;
            results.AddRange(GetQueryStoreTopQueries(db, topN));
        }
        return results;
    }

    private static HealthCheckFinding BuildQueryStoreFinding(QueryStoreStatus s)
    {
        if (s.ActualStateDesc == "OFF")
            return new HealthCheckFinding
            {
                Source = "Native", Category = "Performance", Severity = HealthCheckSeverity.Low,
                Title = $"{s.DatabaseName}: Query Store is not enabled",
                Details = "No persisted query-performance history for this database - the plan-cache scrape and " +
                          "sp_BlitzCache are the only fallback, and both get wiped on restart or memory pressure.",
                Recommendation = "Consider ALTER DATABASE ... SET QUERY_STORE = ON for restart-surviving query history.",
                DatabaseName = s.DatabaseName
            };

        // Bits per sys.database_query_store_options.readonly_reason: 1=db read-only, 2=single-user,
        // 4=emergency mode, 8=secondary replica (all benign/expected states, not alarming), 65536=hit
        // max_storage_size_mb, 131072=too many distinct statements, 262144=in-memory items not yet
        // flushed, 524288=database itself out of disk space (these four are real problems).
        var badReasons = new List<string>();
        if ((s.ReadonlyReason & 65536) != 0) badReasons.Add("hit its configured max storage size");
        if ((s.ReadonlyReason & 131072) != 0) badReasons.Add("too many distinct statements tracked (internal memory limit)");
        if ((s.ReadonlyReason & 524288) != 0) badReasons.Add("the database's own disk ran out of space");

        if (s.ActualStateDesc == "READ_ONLY" && badReasons.Count > 0)
            return new HealthCheckFinding
            {
                Source = "Native", Category = "Performance", Severity = HealthCheckSeverity.High,
                Title = $"{s.DatabaseName}: Query Store is stuck READ_ONLY and silently not capturing new query data",
                Details = $"Reason(s): {string.Join(", ", badReasons)}. Currently using " +
                          $"{s.CurrentStorageSizeMb:N0} of {s.MaxStorageSizeMb:N0} MB allocated.",
                Recommendation = "Increase max_storage_size_mb or purge old Query Store data, then set the database " +
                                  "back to READ_WRITE - this stops recording silently, with no alert of its own.",
                DatabaseName = s.DatabaseName
            };

        return new HealthCheckFinding
        {
            Source = "Native", Category = "Performance", Severity = HealthCheckSeverity.Info,
            Title = $"{s.DatabaseName}: Query Store is active ({s.ActualStateDesc}, capture mode {s.QueryCaptureModeDesc})",
            Details = $"Using {s.CurrentStorageSizeMb:N0} of {s.MaxStorageSizeMb:N0} MB allocated storage.",
            DatabaseName = s.DatabaseName
        };
    }

    // ======================================================================================
    // First Responder Kit EXECs - dynamic, unknown-in-advance result schemas
    // ======================================================================================

    /// <summary>sp_Blitz has no @DatabaseName parameter (only a negative @SkipChecksDatabase skip-list) - runs instance-wide by design.</summary>
    public List<DynamicResultSet> RunBlitz() =>
        ExecDynamic(string.Format(SqlHealthCheck.ExecBlitzFmt, Analyzer.Escape(_opt.ToolsDatabase)), "sp_Blitz");

    public List<DynamicResultSet> RunBlitzIndex() =>
        ExecDynamic(string.Format(SqlHealthCheck.ExecBlitzIndexFmt, Analyzer.Escape(_opt.ToolsDatabase)), "sp_BlitzIndex", scopedToDatabase: true);

    public List<DynamicResultSet> RunBlitzCache() =>
        ExecDynamic(string.Format(SqlHealthCheck.ExecBlitzCacheFmt, Analyzer.Escape(_opt.ToolsDatabase)), "sp_BlitzCache", scopedToDatabase: true);

    public List<DynamicResultSet> RunBlitzLock() =>
        ExecDynamic(string.Format(SqlHealthCheck.ExecBlitzLockFmt, Analyzer.Escape(_opt.ToolsDatabase)), "sp_BlitzLock", scopedToDatabase: true);

    /// <summary>sp_BlitzBackups has no @DatabaseName parameter - it's instance/AG-wide by design (@MSDBName/@AGName instead).</summary>
    public List<DynamicResultSet> RunBlitzBackups() =>
        ExecDynamic(string.Format(SqlHealthCheck.ExecBlitzBackupsFmt, Analyzer.Escape(_opt.ToolsDatabase)), "sp_BlitzBackups");

    /// <summary>Always-run fast snapshot. sp_BlitzFirst has no @DatabaseName either, but @FilterPlansByDatabase
    /// scopes its embedded plan-cache analysis to the connected database.</summary>
    public List<DynamicResultSet> RunBlitzFirstInstant() =>
        ExecDynamic(string.Format(SqlHealthCheck.ExecBlitzFirstInstantFmt, Analyzer.Escape(_opt.ToolsDatabase)),
            "sp_BlitzFirst (instant)", scopedToDatabase: true);

    /// <summary>Supplementary live wait-stat sample - see AnalyzerOptions.BlitzFirstSampleSeconds for the wall-clock tradeoff.</summary>
    public List<DynamicResultSet> RunBlitzFirstSampled()
    {
        var sql = string.Format(SqlHealthCheck.ExecBlitzFirstSampledFmt, Analyzer.Escape(_opt.ToolsDatabase), _opt.BlitzFirstSampleSeconds);
        return ExecDynamic(sql, $"sp_BlitzFirst ({_opt.BlitzFirstSampleSeconds}s live sample)",
            _opt.QueryTimeoutSeconds + _opt.BlitzFirstSampleSeconds, scopedToDatabase: true);
    }

    private List<DynamicResultSet> ExecDynamic(string sql, string namePrefix, int? timeoutOverride = null, bool scopedToDatabase = false)
    {
        using var conn = Open();
        using var cmd = new SqlCommand(sql, conn);
        cmd.CommandTimeout = timeoutOverride ?? _opt.QueryTimeoutSeconds;
        if (scopedToDatabase) cmd.Parameters.AddWithValue("@DbName", ResolveScopeDatabase());
        using var r = cmd.ExecuteReader();
        return ReadAllDynamic(r, namePrefix);
    }

    private static DynamicResultSet ReadDynamic(SqlDataReader r, string name)
    {
        var cols = new List<string>();
        for (var i = 0; i < r.FieldCount; i++) cols.Add(r.GetName(i));

        var rows = new List<Dictionary<string, object?>>();
        while (r.Read())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < cols.Count; i++)
                row[cols[i]] = r.IsDBNull(i) ? null : r.GetValue(i);
            rows.Add(row);
        }

        return new DynamicResultSet { Name = name, ColumnNames = cols, Rows = rows };
    }

    /// <summary>Loops NextResult() since sp_Blitz/sp_BlitzFirst (expert mode) can emit more than one result set.</summary>
    private static List<DynamicResultSet> ReadAllDynamic(SqlDataReader r, string namePrefix)
    {
        var sets = new List<DynamicResultSet>();
        var setNum = 0;
        do
        {
            setNum++;
            if (r.FieldCount == 0) continue;
            sets.Add(ReadDynamic(r, setNum == 1 ? namePrefix : $"{namePrefix} ({setNum})"));
        } while (r.NextResult());
        return sets;
    }

    private static string Trim(string s, int max)
    {
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..max] + "...";
    }
}
