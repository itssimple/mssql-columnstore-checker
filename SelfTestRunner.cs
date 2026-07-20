using System.Diagnostics;

namespace ColumnstoreAnalyzer;

/// <summary>
/// Orchestrates --self-test: a read-only exercise of every code path in the tool, producing a
/// pass/fail diagnostic report instead of the normal output. Designed to be run manually by
/// someone with real access to a live instance (this tool never gets that access itself) - the
/// resulting file can be handed back for review. Every step here is either a plain SELECT, a
/// read-only DBCC command, or an EXEC of an already-installed read-only diagnostic proc (the
/// sp_Blitz family) - nothing here ever creates/alters/inserts/updates/deletes anything, and this
/// mode never calls into the install-flow (HealthCheckRunner's RunInstallFlow is simply never
/// invoked from this code path).
/// </summary>
public static class SelfTestRunner
{
    private const int MaxTablesToExercise = 3;

    public static SelfTestResult Run(AnalyzerOptions opt)
    {
        var result = new SelfTestResult { ServerName = opt.Server, GeneratedAt = DateTime.Now };

        RunColumnstoreChecks(opt, result);
        RunHealthCheckNativeChecks(result, new HealthCheckAnalyzer(opt));
        RunFrkChecks(result, new HealthCheckAnalyzer(opt));
        RunPermissionsChecks(result, new PermissionsAnalyzer(opt));

        return result;
    }

    // ======================================================================================
    // Columnstore pipeline - limited to a small sample of tables for self-test speed.
    // ======================================================================================

    private static void RunColumnstoreChecks(AnalyzerOptions opt, SelfTestResult result)
    {
        var analyzer = new Analyzer(opt);

        if (Step(result, "Columnstore", "GetServerInfo", () =>
            {
                var (major, startTime, uptimeDays) = analyzer.GetServerInfo();
                return $"major version {major}, started {startTime:yyyy-MM-dd}, {uptimeDays} day(s) uptime";
            }) == null) return;

        var tables = StepValue(result, "Columnstore", "DiscoverTables", analyzer.DiscoverTables,
            t => $"{t.Count} candidate table(s) discovered");
        if (tables == null || tables.Count == 0) return;

        var sample = tables.Take(MaxTablesToExercise).ToList();
        Note(result, "Columnstore", $"Exercising {sample.Count} of {tables.Count} discovered table(s) (self-test cap: {MaxTablesToExercise})");

        foreach (var t in sample)
        {
            var label = t.FullName;
            Step(result, "Columnstore", $"LoadOperationalStats [{label}]", () => { analyzer.LoadOperationalStats(t); return "ok"; });
            Step(result, "Columnstore", $"LoadIndexes [{label}]", () => { analyzer.LoadIndexes(t); return $"{t.Indexes.Count} index(es)"; });
            Step(result, "Columnstore", $"LoadColumns [{label}]", () => { analyzer.LoadColumns(t); return $"{t.Columns.Count} column(s)"; });

            if (t.HasColumnstore)
            {
                Note(result, "Columnstore", $"{label}: already has a columnstore index, skipping cardinality sampling");
                continue;
            }

            Step(result, "Columnstore", $"AnalyzeColumnCardinality [{label}]", () =>
            {
                analyzer.AnalyzeColumnCardinality(t);
                return $"sampled {t.SampledRows:N0} row(s) ({(t.WasSampled ? "TABLESAMPLE" : "full scan")})";
            });
        }

        Step(result, "Columnstore", "MatchPlanCacheQueries", () =>
        {
            analyzer.MatchPlanCacheQueries(sample);
            return $"matched {sample.Count(t => t.ReferencingQueries.Count > 0)} of {sample.Count} table(s)";
        });

        Step(result, "Columnstore", "MatchQueryStoreQueries", () =>
        {
            var caveat = analyzer.MatchQueryStoreQueries(sample);
            return caveat ?? $"matched {sample.Count(t => t.ReferencingQueries.Count > 0)} of {sample.Count} table(s) (plan cache + Query Store combined)";
        });
    }

    // ======================================================================================
    // Health-check native checks (all read-only DMV/catalog-view queries)
    // ======================================================================================

    private static void RunHealthCheckNativeChecks(SelfTestResult result, HealthCheckAnalyzer hc)
    {
        Step(result, "HealthCheck-Native", "RunWaitStats", () => $"{hc.RunWaitStats().Count} wait type(s)");
        Step(result, "HealthCheck-Native", "RunCheckdbAge", () => $"{hc.RunCheckdbAge().Count} finding(s)");
        Step(result, "HealthCheck-Native", "RunBackupRecency", () => $"{hc.RunBackupRecency().Count} finding(s)");
        Step(result, "HealthCheck-Native", "RunTempdbConfig", () => $"{hc.RunTempdbConfig().Count} finding(s)");
        Step(result, "HealthCheck-Native", "RunConfigSmells", () => $"{hc.RunConfigSmells().Count} finding(s)");
        Step(result, "HealthCheck-Native", "RunAgentJobFailures", () => $"{hc.RunAgentJobFailures().Count} finding(s)");
        Step(result, "HealthCheck-Native", "RunJobOwnerAudit", () => $"{hc.RunJobOwnerAudit().Count} finding(s)");
        Step(result, "HealthCheck-Native", "RunSecurityInventory", () => $"{hc.RunSecurityInventory().Count} finding(s)");
        Step(result, "HealthCheck-Native", "RunTopologyRollup", () => $"{hc.RunTopologyRollup().Count} finding(s)");
        Step(result, "HealthCheck-Native", "RunAvailabilityGroupHealth",
            () => $"{hc.RunAvailabilityGroupHealth(out var replicas, out var databases).Count} finding(s), {replicas.Count} replica(s), {databases.Count} database(s)");
        Step(result, "HealthCheck-Native", "RunReplicationErrors", () => $"{hc.RunReplicationErrors().Count} finding(s)");
    }

    // ======================================================================================
    // FRK detection + instant-only EXEC of whatever's installed. Never the 30s live sample -
    // self-test always stays fast and predictable regardless of --blitzfirst-seconds.
    // ======================================================================================

    private static void RunFrkChecks(SelfTestResult result, HealthCheckAnalyzer hc)
    {
        var components = StepValue(result, "HealthCheck-FRK", "DetectTools", hc.DetectTools,
            c => $"{c.Count(x => x.Installed)}/{c.Count} tool(s) detected as installed");
        if (components == null) return;

        Step(result, "HealthCheck-FRK", "DetectOlaHallengren", () => $"{hc.DetectOlaHallengren().Count(j => j.Found)} job(s) found");

        var installed = components.Where(c => c.Installed).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        RunOneIfInstalled(result, installed, "sp_Blitz", hc.RunBlitz);
        RunOneIfInstalled(result, installed, "sp_BlitzIndex", hc.RunBlitzIndex);
        RunOneIfInstalled(result, installed, "sp_BlitzCache", hc.RunBlitzCache);
        RunOneIfInstalled(result, installed, "sp_BlitzFirst", hc.RunBlitzFirstInstant,
            label: "sp_BlitzFirst (instant only - self-test never runs the live sample)");
    }

    private static void RunOneIfInstalled(SelfTestResult result, HashSet<string> installed, string procName,
        Func<List<DynamicResultSet>> exec, string? label = null)
    {
        if (!installed.Contains(procName))
        {
            Note(result, "HealthCheck-FRK", $"{procName}: not installed, skipped");
            return;
        }

        Step(result, "HealthCheck-FRK", label ?? procName, () =>
        {
            var sets = exec();
            return $"{sets.Count} result set(s), {sets.Sum(s => s.Rows.Count)} total row(s)";
        });
    }

    // ======================================================================================
    // Permissions inventory - same catalog-view queries the standalone --permissions-report uses.
    // ======================================================================================

    private static void RunPermissionsChecks(SelfTestResult result, PermissionsAnalyzer pa)
    {
        StepValue(result, "Permissions", "GetServerPrincipals", pa.GetServerPrincipals, p => $"{p.Count} server principal(s)");

        var loginsBySid = StepValue(result, "Permissions", "GetServerLoginsBySid", pa.GetServerLoginsBySid,
            m => $"{m.Count} login sid(s) mapped");
        var databases = StepValue(result, "Permissions", "ListDatabases", pa.ListDatabases,
            d => $"{d.Count} online database(s)");
        if (databases == null || loginsBySid == null) return;

        foreach (var db in databases)
        {
            Step(result, "Permissions", $"GetDatabaseUsers [{db}]", () => $"{pa.GetDatabaseUsers(db, loginsBySid).Count} user(s)");
            Step(result, "Permissions", $"GetObjectPermissions [{db}]", () => $"{pa.GetObjectPermissions(db).Count} explicit permission(s)");
        }
    }

    // ======================================================================================
    // Step-execution helpers
    // ======================================================================================

    private static string? Step(SelfTestResult result, string category, string step, Func<string> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var summary = action();
            result.Steps.Add(new SelfTestStepResult
            {
                Category = category, Step = step, Success = true, ElapsedMs = sw.ElapsedMilliseconds, Summary = summary
            });
            Console.WriteLine($"  [ok]   {category,-16} {step,-45} {summary} ({sw.ElapsedMilliseconds}ms)");
            return summary;
        }
        catch (Exception ex)
        {
            result.Steps.Add(new SelfTestStepResult
            {
                Category = category, Step = step, Success = false, ElapsedMs = sw.ElapsedMilliseconds,
                ErrorType = ex.GetType().Name, ErrorMessage = ex.Message
            });
            Console.WriteLine($"  [FAIL] {category,-16} {step,-45} {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static T? StepValue<T>(SelfTestResult result, string category, string step, Func<T> action, Func<T, string> summarize)
        where T : class
    {
        T? value = null;
        var ok = Step(result, category, step, () =>
        {
            value = action();
            return summarize(value);
        });
        return ok != null ? value : null;
    }

    private static void Note(SelfTestResult result, string category, string message)
    {
        result.Steps.Add(new SelfTestStepResult { Category = category, Step = "note", Success = true, Summary = message });
        Console.WriteLine($"  [note] {category,-16} {message}");
    }
}
