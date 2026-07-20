namespace ColumnstoreAnalyzer;

/// <summary>
/// T-SQL for the opt-in health-check stage: sibling to Sql.cs, kept separate so that file
/// stays scoped to columnstore analysis. Format-string consts take an already-escaped
/// (via Analyzer.Escape) database/identifier via {0} (and occasionally {1}) - never
/// interpolate a raw identifier directly.
/// </summary>
internal static class SqlHealthCheck
{
    /// <summary>Detects installed First Responder Kit procs + sp_WhoIsActive in the given database (param: {0} = escaped db name).</summary>
    public const string DetectToolsFmt = @"
SELECT p.name, p.create_date, p.modify_date
FROM {0}.sys.procedures p
WHERE p.name LIKE 'sp_Blitz%' OR p.name = 'sp_WhoIsActive';";

    // ---- FRK EXEC templates ({0} = escaped tools database). @DbName is bound as a real SqlParameter
    // by the caller wherever the proc actually supports a database-scoping parameter (verified against
    // the pinned FRK source - sp_Blitz and sp_BlitzBackups do NOT accept one, so those stay instance-wide). ----
    public const string ExecBlitzFmt = "EXEC {0}.dbo.sp_Blitz;"; // no @DatabaseName param - only a negative @SkipChecksDatabase skip-list
    public const string ExecBlitzIndexFmt = "EXEC {0}.dbo.sp_BlitzIndex @DatabaseName = @DbName;";
    public const string ExecBlitzCacheFmt = "EXEC {0}.dbo.sp_BlitzCache @DatabaseName = @DbName;";
    public const string ExecBlitzFirstInstantFmt = "EXEC {0}.dbo.sp_BlitzFirst @ExpertMode = 1, @SinceStartup = 1, @FilterPlansByDatabase = @DbName;";
    /// <summary>{0} = escaped tools database, {1} = sample seconds (int, not user-escaped - caller must validate it's numeric).</summary>
    public const string ExecBlitzFirstSampledFmt = "EXEC {0}.dbo.sp_BlitzFirst @Seconds = {1}, @ExpertMode = 1, @FilterPlansByDatabase = @DbName;";
    public const string ExecBlitzLockFmt = "EXEC {0}.dbo.sp_BlitzLock @DatabaseName = @DbName;";
    public const string ExecBlitzBackupsFmt = "EXEC {0}.dbo.sp_BlitzBackups;"; // no @DatabaseName param - instance/AG-wide by design (@MSDBName/@AGName instead)

    /// <summary>Ola Hallengren maintenance-job presence (no EXEC needed - just msdb metadata).
    /// sysjobs.enabled is tinyint, not bit - cast explicitly so SqlDataReader.GetBoolean works.</summary>
    public const string DetectOlaJobs = @"
SELECT j.job_id, j.name, CAST(j.enabled AS bit) AS enabled
FROM msdb.dbo.sysjobs j
WHERE j.name LIKE 'DatabaseBackup - %' OR j.name LIKE 'DatabaseIntegrityCheck - %' OR j.name LIKE 'IndexOptimize - %'
ORDER BY j.name;";

    /// <summary>Most recent outcome for one job (param: @JobId).</summary>
    public const string OlaJobLastRun = @"
SELECT TOP (1)
    CASE h.run_status WHEN 0 THEN 'Failed' WHEN 1 THEN 'Succeeded' WHEN 2 THEN 'Retry' WHEN 3 THEN 'Canceled' ELSE 'Unknown' END AS last_run_outcome,
    msdb.dbo.agent_datetime(h.run_date, h.run_time) AS last_run_time
FROM msdb.dbo.sysjobhistory h
WHERE h.job_id = @JobId AND h.step_id = 0
ORDER BY h.run_date DESC, h.run_time DESC;";

    /// <summary>Next scheduled run for one job (param: @JobId). Unlike sysjobhistory, sysjobactivity
    /// stores next_scheduled_run_date as a plain datetime already - no paired _time column, no agent_datetime() needed.</summary>
    public const string OlaJobNextRun = @"
SELECT TOP (1) a.next_scheduled_run_date AS next_run_time
FROM msdb.dbo.sysjobactivity a
WHERE a.job_id = @JobId AND a.next_scheduled_run_date IS NOT NULL
ORDER BY a.start_execution_date DESC;";

    /// <summary>Top-N non-benign cumulative wait stats (param: @N). Ignore list matches the well-known Ozar/Berry set.</summary>
    public const string WaitStatsTopN = @"
SELECT TOP (@N) wait_type, wait_time_ms, waiting_tasks_count, signal_wait_time_ms
FROM sys.dm_os_wait_stats
WHERE waiting_tasks_count > 0
  AND wait_type NOT IN (
    'SLEEP_TASK','BROKER_TASK_STOP','BROKER_TO_FLUSH','BROKER_EVENTHANDLER',
    'LAZYWRITER_SLEEP','CHECKPOINT_QUEUE','REQUEST_FOR_DEADLOCK_SEARCH','XE_TIMER_EVENT',
    'XE_DISPATCHER_WAIT','XE_DISPATCHER_JOIN','LOGMGR_QUEUE','CLR_AUTO_EVENT','CLR_MANUAL_EVENT',
    'CLR_SEMAPHORE','SQLTRACE_BUFFER_FLUSH','WAITFOR','DIRTY_PAGE_POLL','HADR_FILESTREAM_IOMGR_IOCOMPLETION',
    'RESOURCE_QUEUE','ONDEMAND_TASK_QUEUE','SLEEP_SYSTEMTASK','SLEEP_BPOOL_FLUSH','DISPATCHER_QUEUE_SEMAPHORE',
    'FT_IFTS_SCHEDULER_IDLE_WAIT','SP_SERVER_DIAGNOSTICS_SLEEP','QDS_PERSIST_TASK_MAIN_LOOP_SLEEP',
    'DBMIRROR_EVENTS_QUEUE','DBMIRROR_WORKER_QUEUE','DBMIRRORING_CMD','BROKER_RECEIVE_WAITFOR',
    'PWAIT_ALL_COMPONENTS_INITIALIZED','SQLTRACE_INCREMENTAL_FLUSH_SLEEP','SQLTRACE_WAIT_ENTRIES',
    'WAIT_FOR_RESULTS','WAIT_XTP_RECOVERY','BROKER_TRANSMITTER','WAIT_XTP_HOST_WAIT',
    'WAIT_XTP_CKPT_CLOSE','XE_LIVE_TARGET_TVF'
  )
ORDER BY wait_time_ms DESC;";

    /// <summary>Last known-good CHECKDB for one database. {0} = single-quote-escaped db name (DBCC takes a string literal, not a bracketed identifier).</summary>
    public const string CheckdbLastGoodFmt = "DBCC DBINFO ('{0}') WITH TABLERESULTS;";

    /// <summary>Backup recency per user database, all databases (caller filters down to current DB unless --health-check-all-databases).</summary>
    public const string BackupRecency = @"
SELECT
    d.name AS database_name,
    d.recovery_model_desc,
    MAX(CASE WHEN b.type = 'D' THEN b.backup_finish_date END) AS last_full_backup,
    MAX(CASE WHEN b.type = 'I' THEN b.backup_finish_date END) AS last_diff_backup,
    MAX(CASE WHEN b.type = 'L' THEN b.backup_finish_date END) AS last_log_backup
FROM sys.databases d
LEFT JOIN msdb.dbo.backupset b ON b.database_name = d.name
WHERE d.database_id > 4 AND d.state = 0
GROUP BY d.name, d.recovery_model_desc
ORDER BY d.name;";

    public const string TempdbConfig = @"
SELECT mf.name, mf.physical_name, CAST(mf.size AS bigint) * 8 / 1024 AS size_mb, mf.growth, mf.is_percent_growth,
       (SELECT cpu_count FROM sys.dm_os_sys_info) AS cpu_count,
       (SELECT COUNT(*) FROM sys.master_files WHERE database_id = 2 AND type_desc = 'ROWS') AS data_file_count
FROM sys.master_files mf
WHERE mf.database_id = 2
ORDER BY mf.file_id;";

    public const string ServerConfigSmells = @"
SELECT name, CAST(value AS bigint) AS value, CAST(value_in_use AS bigint) AS value_in_use
FROM sys.configurations
WHERE name IN ('max degree of parallelism', 'cost threshold for parallelism',
               'max server memory (MB)', 'min server memory (MB)', 'optimize for ad hoc workloads');";

    public const string DatabaseFlagSmells = @"
SELECT name, is_auto_shrink_on, is_auto_close_on, is_auto_create_stats_on, is_auto_update_stats_on, compatibility_level
FROM sys.databases
WHERE database_id > 4 AND state = 0;";

    /// <summary>Failed agent job steps in the last @Days days.</summary>
    public const string AgentJobFailures = @"
SELECT j.name AS job_name, msdb.dbo.agent_datetime(h.run_date, h.run_time) AS run_time, h.message
FROM msdb.dbo.sysjobhistory h
JOIN msdb.dbo.sysjobs j ON j.job_id = h.job_id
WHERE h.step_id = 0 AND h.run_status = 0
  AND msdb.dbo.agent_datetime(h.run_date, h.run_time) >= DATEADD(DAY, -@Days, SYSDATETIME())
ORDER BY h.run_date DESC, h.run_time DESC;";

    /// <summary>Every SQL Agent job with its owning login - the "landmine" check for a departing owner's personal jobs.
    /// sysjobs.enabled is tinyint, not bit - cast explicitly so SqlDataReader.GetBoolean works.</summary>
    public const string JobOwnerAudit = @"
SELECT j.name AS job_name, CAST(j.enabled AS bit) AS enabled, sp.name AS owner_login, sp.type_desc AS owner_type
FROM msdb.dbo.sysjobs j
JOIN sys.server_principals sp ON sp.sid = j.owner_sid
ORDER BY j.name;";

    public const string SysadminLogins = @"
SELECT sp.name, sp.type_desc, sp.is_disabled
FROM sys.server_principals sp
JOIN sys.server_role_members rm ON rm.member_principal_id = sp.principal_id
JOIN sys.server_principals r ON r.principal_id = rm.role_principal_id AND r.name = 'sysadmin'
WHERE sp.type IN ('S','U','G')
ORDER BY sp.name;";

    public const string DisabledLogins = @"
SELECT name, type_desc, create_date
FROM sys.server_principals
WHERE is_disabled = 1 AND type IN ('S','U')
ORDER BY name;";

    /// <summary>Orphaned users in the currently-connected database (SQL logins with no matching server principal).</summary>
    public const string OrphanedUsers = @"
SELECT dp.name AS user_name, dp.type_desc
FROM sys.database_principals dp
LEFT JOIN sys.server_principals sp ON sp.sid = dp.sid
WHERE dp.type IN ('S','U') AND dp.authentication_type_desc = 'INSTANCE'
  AND sp.sid IS NULL AND dp.principal_id > 4
ORDER BY dp.name;";

    public const string LinkedServers = @"
SELECT s.name, s.product, s.provider, s.data_source,
       MAX(CASE WHEN ll.uses_self_credential = 1 THEN 1 ELSE 0 END) AS uses_self_credential
FROM sys.servers s
LEFT JOIN sys.linked_logins ll ON ll.server_id = s.server_id
WHERE s.is_linked = 1
GROUP BY s.name, s.product, s.provider, s.data_source
ORDER BY s.name;";

    /// <summary>Server-wide topology rollup: replication, CDC, Availability Groups.</summary>
    public const string TopologyRollup = @"
SELECT
    (SELECT COUNT(*) FROM sys.databases WHERE is_published = 1 OR is_subscribed = 1 OR is_distributor = 1) AS replicated_db_count,
    (SELECT COUNT(*) FROM sys.databases WHERE is_cdc_enabled = 1) AS cdc_db_count,
    (SELECT COUNT(*) FROM sys.dm_hadr_availability_replica_states) AS ag_replica_count;";
}
