namespace ColumnstoreAnalyzer;

internal static class Sql
{
    /// <summary>Large user tables + columnstore/replication flags + workload aggregates, one row per table.</summary>
    public const string DiscoverTables = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

;WITH sizes AS (
    SELECT
        t.object_id,
        s.name AS schema_name,
        t.name AS table_name,
        SUM(CASE WHEN ps.index_id IN (0,1) THEN ps.row_count ELSE 0 END) AS row_count,
        CAST(SUM(ps.reserved_page_count) * 8 / 1024.0 AS decimal(18,1)) AS total_size_mb,
        CAST(SUM(CASE WHEN ps.index_id IN (0,1) THEN ps.reserved_page_count ELSE 0 END) * 8 / 1024.0 AS decimal(18,1)) AS base_data_mb,
        CAST(SUM(CASE WHEN ps.index_id > 1 THEN ps.reserved_page_count ELSE 0 END) * 8 / 1024.0 AS decimal(18,1)) AS nonclustered_mb,
        MAX(CAST(t.is_replicated AS int))     AS is_replicated,
        MAX(CAST(t.is_tracked_by_cdc AS int)) AS is_cdc_tracked
    FROM sys.tables t
    JOIN sys.schemas s ON s.schema_id = t.schema_id
    JOIN sys.dm_db_partition_stats ps ON ps.object_id = t.object_id
    WHERE t.is_ms_shipped = 0
      AND t.is_memory_optimized = 0
    GROUP BY t.object_id, s.name, t.name
    HAVING SUM(CASE WHEN ps.index_id IN (0,1) THEN ps.row_count ELSE 0 END) >= @MinRowCount
        OR SUM(ps.reserved_page_count) * 8 / 1024 >= @MinTableSizeMB
),
usage AS (
    SELECT sz.object_id,
           ISNULL(SUM(us.user_seeks),0)   AS user_seeks,
           ISNULL(SUM(us.user_scans),0)   AS user_scans,
           ISNULL(SUM(us.user_lookups),0) AS user_lookups,
           ISNULL(SUM(us.user_updates),0) AS user_updates
    FROM sizes sz
    LEFT JOIN sys.dm_db_index_usage_stats us
           ON us.object_id = sz.object_id AND us.database_id = DB_ID()
    GROUP BY sz.object_id
),
missing AS (
    SELECT mid.object_id, COUNT(*) AS suggestions
    FROM sys.dm_db_missing_index_details mid
    WHERE mid.database_id = DB_ID()
    GROUP BY mid.object_id
)
SELECT TOP (@TopN)
    sz.object_id, sz.schema_name, sz.table_name,
    sz.row_count, sz.total_size_mb, sz.base_data_mb, sz.nonclustered_mb,
    sz.is_replicated, sz.is_cdc_tracked,
    CASE WHEN EXISTS (SELECT 1 FROM sys.indexes i
                      WHERE i.object_id = sz.object_id AND i.type IN (5,6))
         THEN 1 ELSE 0 END AS has_columnstore,
    u.user_seeks, u.user_scans, u.user_lookups, u.user_updates,
    ISNULL(m.suggestions, 0) AS missing_index_suggestions
FROM sizes sz
JOIN usage u   ON u.object_id = sz.object_id
LEFT JOIN missing m ON m.object_id = sz.object_id
ORDER BY sz.total_size_mb DESC;";

    /// <summary>Lock/latch contention + write mix for one table (parameter: @ObjectId).</summary>
    public const string OperationalStats = @"
SELECT
    ISNULL(SUM(os.row_lock_wait_in_ms), 0)      AS row_lock_wait_ms,
    ISNULL(SUM(os.page_lock_wait_in_ms), 0)     AS page_lock_wait_ms,
    ISNULL(SUM(os.page_io_latch_wait_in_ms), 0) AS page_io_latch_wait_ms,
    ISNULL(SUM(os.leaf_insert_count), 0)        AS leaf_inserts,
    ISNULL(SUM(os.leaf_update_count), 0)        AS leaf_updates,
    ISNULL(SUM(os.leaf_delete_count + os.leaf_ghost_count), 0) AS leaf_deletes
FROM sys.dm_db_index_operational_stats(DB_ID(), @ObjectId, NULL, NULL) os;";

    /// <summary>Column metadata for one table (parameter: @ObjectId).</summary>
    public const string GetColumns = @"
SELECT
    c.name AS column_name,
    ty.name AS data_type,
    c.max_length,
    c.is_computed,
    CASE WHEN ty.name IN ('text','ntext','image','xml','geometry','geography','hierarchyid','sql_variant')
              OR ((ty.name IN ('varchar','nvarchar','varbinary')) AND c.max_length = -1)
         THEN 1 ELSE 0 END AS is_lob_or_unsupported
FROM sys.columns c
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE c.object_id = @ObjectId
ORDER BY c.column_id;";

    /// <summary>Index inventory for one table (parameter: @ObjectId).</summary>
    public const string GetIndexes = @"
SELECT
    i.name AS index_name,
    i.type_desc,
    CAST(SUM(ps.reserved_page_count) * 8 / 1024.0 AS decimal(18,1)) AS size_mb,
    (SELECT COUNT(*) FROM sys.index_columns ic
      WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0) AS key_column_count,
    (SELECT COUNT(*) FROM sys.index_columns ic
      WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1) AS included_column_count,
    ISNULL(STUFF((SELECT ', ' + c.name
           FROM sys.index_columns ic
           JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
           WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
           ORDER BY ic.key_ordinal
           FOR XML PATH(''), TYPE).value('.','nvarchar(max)'), 1, 2, ''), '') AS key_columns,
    ISNULL(STUFF((SELECT ', ' + c.name
           FROM sys.index_columns ic
           JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
           WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
           FOR XML PATH(''), TYPE).value('.','nvarchar(max)'), 1, 2, ''), '') AS included_columns,
    ISNULL(us.user_seeks, 0)   AS user_seeks,
    ISNULL(us.user_scans, 0)   AS user_scans,
    ISNULL(us.user_lookups, 0) AS user_lookups,
    ISNULL(us.user_updates, 0) AS user_updates
FROM sys.indexes i
JOIN sys.dm_db_partition_stats ps ON ps.object_id = i.object_id AND ps.index_id = i.index_id
LEFT JOIN sys.dm_db_index_usage_stats us
       ON us.object_id = i.object_id AND us.index_id = i.index_id AND us.database_id = DB_ID()
WHERE i.object_id = @ObjectId AND i.type > 0
GROUP BY i.object_id, i.index_id, i.name, i.type_desc,
         us.user_seeks, us.user_scans, us.user_lookups, us.user_updates
ORDER BY SUM(ps.reserved_page_count) DESC;";

    /// <summary>All cached statements for this database; matched to tables client-side.</summary>
    public const string PlanCacheStatements = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT
    qs.execution_count,
    qs.total_logical_reads,
    CAST(qs.total_worker_time  / 1000.0 AS float) AS total_cpu_ms,
    CAST(qs.total_elapsed_time / 1000.0 AS float) AS total_elapsed_ms,
    qs.last_execution_time,
    SUBSTRING(st.text,
              (qs.statement_start_offset/2) + 1,
              ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text)
                ELSE qs.statement_end_offset END - qs.statement_start_offset)/2) + 1) AS statement_text
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
WHERE (st.dbid = DB_ID() OR st.dbid IS NULL);";

    public const string ServerInfo = @"
SELECT
    CAST(SERVERPROPERTY('ProductMajorVersion') AS nvarchar(10)) AS major_version,
    sqlserver_start_time,
    DATEDIFF(DAY, sqlserver_start_time, SYSDATETIME()) AS uptime_days
FROM sys.dm_os_sys_info;";
}
