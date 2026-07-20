using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ColumnstoreAnalyzer;

/// <summary>
/// Orchestrates the whole opt-in health-check stage: tool/job detection, native checks,
/// tribal-knowledge inventory, FRK EXECs, and (if requested) the install flow. Every step
/// is wrapped in its own try/catch and prints a short progress line - one failing check
/// must never abort the others, matching Program.Run's per-table convention.
/// </summary>
public static class HealthCheckRunner
{
    public static HealthCheckResult Run(AnalyzerOptions opt)
    {
        var hc = new HealthCheckAnalyzer(opt);
        var result = new HealthCheckResult();

        Step("tool detection (FRK/sp_WhoIsActive)", () => result.Components.AddRange(hc.DetectTools()));
        if (!opt.SkipOla)
            Step("Ola Hallengren maintenance jobs", () => result.MaintenanceJobs.AddRange(hc.DetectOlaHallengren()));

        Step("wait stats", () => result.Findings.AddRange(hc.RunWaitStats()));
        Step("CHECKDB age", () => result.Findings.AddRange(hc.RunCheckdbAge()));
        Step("backup recency", () => result.Findings.AddRange(hc.RunBackupRecency()));
        Step("tempdb config", () => result.Findings.AddRange(hc.RunTempdbConfig()));
        Step("server/database config smells", () => result.Findings.AddRange(hc.RunConfigSmells()));
        Step("agent job failure history", () => result.Findings.AddRange(hc.RunAgentJobFailures()));
        Step("job ownership audit", () => result.Findings.AddRange(hc.RunJobOwnerAudit()));
        Step("security/access inventory", () => result.Findings.AddRange(hc.RunSecurityInventory()));
        Step("topology rollup", () => result.Findings.AddRange(hc.RunTopologyRollup()));

        if (!opt.SkipFrk) RunFrkChecks(hc, opt, result);

        if (opt.InstallMissingTools) RunInstallFlow(opt, result);

        FinalizeFindings(opt, result);

        return result;
    }

    /// <summary>Applies uniformly across every finding regardless of which check produced it: (1) drop findings
    /// that are clearly scoped to a database other than the one connected to, unless --health-check-all-databases
    /// was passed - e.g. sp_Blitz has no @DatabaseName parameter so it always reports on every database on the
    /// instance, and a couple of native checks (auto-shrink/auto-close) do the same; (2) drop exact-duplicate
    /// findings. Raw result sets (sp_BlitzCache/sp_BlitzFirst) are untouched - only Findings gets cleaned up.</summary>
    private static void FinalizeFindings(AnalyzerOptions opt, HealthCheckResult result)
    {
        if (!opt.HealthCheckAllDatabases)
            result.Findings.RemoveAll(f =>
                f.DatabaseName != null && !f.DatabaseName.Equals(opt.Database, StringComparison.OrdinalIgnoreCase));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        result.Findings.RemoveAll(f => !seen.Add(DedupeKey(f)));
    }

    private static string DedupeKey(HealthCheckFinding f) =>
        $"{f.Source}{f.Category}{f.Severity}{f.Title}{f.Details}{f.DatabaseName}{f.ObjectName}";

    private static void Step(string label, Action step)
    {
        Console.Write($"  - {label} ... ");
        try
        {
            step();
            Console.WriteLine("ok");
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED (non-fatal): " + ex.Message);
        }
    }

    // ======================================================================================
    // First Responder Kit: EXEC whatever's installed, skip (with a note) whatever isn't.
    // sp_WhoIsActive is deliberately never EXEC'd - see DetectTools/HealthCheckAnalyzer.
    // ======================================================================================

    private static void RunFrkChecks(HealthCheckAnalyzer hc, AnalyzerOptions opt, HealthCheckResult result)
    {
        var installed = result.Components.Where(c => c.Installed).Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        RunOneBlitzProc(result, installed, "sp_Blitz", hc.RunBlitz);
        RunOneBlitzProc(result, installed, "sp_BlitzIndex", hc.RunBlitzIndex);
        RunOneBlitzProc(result, installed, "sp_BlitzCache", hc.RunBlitzCache, rawOnly: true);
        RunBlitzFirst(hc, opt, result, installed);

        if (opt.IncludeBlitzLock) RunOneBlitzProc(result, installed, "sp_BlitzLock", hc.RunBlitzLock);
        if (opt.IncludeBlitzBackups) RunOneBlitzProc(result, installed, "sp_BlitzBackups", hc.RunBlitzBackups);
    }

    /// <summary>Always captures the fast instant snapshot; additionally captures a live @Seconds= sample by
    /// default (opt.BlitzFirstSampleSeconds, 0 to skip) - handled separately from RunOneBlitzProc so a missing
    /// sp_BlitzFirst only prints one "not installed" line instead of two.</summary>
    private static void RunBlitzFirst(HealthCheckAnalyzer hc, AnalyzerOptions opt, HealthCheckResult result, HashSet<string> installed)
    {
        if (!installed.Contains("sp_BlitzFirst"))
        {
            Console.WriteLine("  - sp_BlitzFirst ... not installed, skipping (see report for install instructions)");
            return;
        }

        Step("sp_BlitzFirst (instant mode)", () => result.RawResultSets.AddRange(hc.RunBlitzFirstInstant()));

        if (opt.BlitzFirstSampleSeconds > 0)
            Step($"sp_BlitzFirst ({opt.BlitzFirstSampleSeconds}s live sample, this will pause)",
                () => result.RawResultSets.AddRange(hc.RunBlitzFirstSampled()));
    }

    private static void RunOneBlitzProc(HealthCheckResult result, HashSet<string> installed, string procName,
        Func<List<DynamicResultSet>> exec, bool rawOnly = false, string? label = null)
    {
        if (!installed.Contains(procName))
        {
            Console.WriteLine($"  - {procName} ... not installed, skipping (see report for install instructions)");
            return;
        }

        Step(label ?? procName, () =>
        {
            foreach (var rs in exec())
            {
                if (!rawOnly && BlitzAdapter.LooksFindingShaped(rs))
                    result.Findings.AddRange(BlitzAdapter.ToFindings(procName, rs));
                else
                    result.RawResultSets.Add(rs);
            }
        });
    }

    // ======================================================================================
    // Install flow - gated behind --health-check --install-missing-tools, never on by default.
    // ======================================================================================

    private static void RunInstallFlow(AnalyzerOptions opt, HealthCheckResult result)
    {
        if (result.Components.Any(c => !c.Installed && c.Name.StartsWith("sp_Blitz")))
            InstallOne(opt, result, InstallSources.FirstResponderKit, "First Responder Kit");

        if (result.MaintenanceJobs.Any(j => !j.Found))
            InstallOne(opt, result, InstallSources.OlaHallengrenMaintenanceSolution, "Ola Hallengren Maintenance Solution");
    }

    private static void InstallOne(AnalyzerOptions opt, HealthCheckResult result, InstallSource source, string shortName)
    {
        Console.WriteLine();
        Console.WriteLine($"  {shortName} appears to be missing. Install flow:");
        Console.WriteLine($"    Component:       {source.Component}");
        Console.WriteLine($"    Pinned version:  {source.PinnedVersion}");
        Console.WriteLine($"    Source:          {source.ScriptUrl}");
        Console.WriteLine($"    Will execute in: {opt.ToolsDatabase} (and/or msdb for any Agent jobs it creates)");
        Console.WriteLine($"    Notes:           {source.Notes}");

        if (!Confirm($"    Type YES to download, verify (SHA-256), and install {shortName}: "))
        {
            Console.WriteLine("    Skipped (not confirmed).");
            result.InstallActions.Add(new InstallAction
            {
                Component = source.Component, PinnedVersion = source.PinnedVersion,
                When = DateTime.Now, Succeeded = false, Detail = "Skipped - not confirmed by operator"
            });
            return;
        }

        string script;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            script = http.GetStringAsync(source.ScriptUrl).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    Download failed (non-fatal): {ex.Message}. Install manually from {source.ScriptUrl}");
            result.InstallActions.Add(new InstallAction
            {
                Component = source.Component, PinnedVersion = source.PinnedVersion,
                When = DateTime.Now, Succeeded = false, Detail = "Download failed: " + ex.Message
            });
            return;
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script))).ToLowerInvariant();
        if (!actualHash.Equals(source.Sha256Hex, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"    ABORTED: downloaded script checksum ({actualHash}) does not match the pinned value " +
                              $"({source.Sha256Hex}). Upstream may have changed - review manually before updating InstallSources.cs.");
            result.InstallActions.Add(new InstallAction
            {
                Component = source.Component, PinnedVersion = source.PinnedVersion,
                When = DateTime.Now, Succeeded = false, Detail = $"Checksum mismatch: got {actualHash}"
            });
            return;
        }

        try
        {
            ExecuteBatchedScript(opt, script);
            Console.WriteLine($"    {shortName} installed successfully.");
            result.InstallActions.Add(new InstallAction
            {
                Component = source.Component, PinnedVersion = source.PinnedVersion,
                When = DateTime.Now, Succeeded = true, Detail = "Installed via pinned script, checksum verified"
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    Install FAILED (non-fatal): {ex.Message}");
            result.InstallActions.Add(new InstallAction
            {
                Component = source.Component, PinnedVersion = source.PinnedVersion,
                When = DateTime.Now, Succeeded = false, Detail = "Execution failed: " + ex.Message
            });
        }
    }

    /// <summary>Splits on a 'GO' line (the SSMS/sqlcmd batch separator - Microsoft.Data.SqlClient has no native
    /// concept of it) and executes each batch in order.</summary>
    private static void ExecuteBatchedScript(AnalyzerOptions opt, string script)
    {
        using var conn = ConnectionFactory.Open(opt);
        var batches = Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        foreach (var batch in batches)
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            using var cmd = new SqlCommand(batch, conn);
            cmd.CommandTimeout = opt.QueryTimeoutSeconds;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Stdin-redirect-aware confirmation, mirroring PasswordInput's safety pattern: a piped/non-interactive
    /// context can't safely confirm a permission-escalating action, so it aborts rather than silently proceeding.</summary>
    private static bool Confirm(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            Console.WriteLine();
            Console.WriteLine("    Non-interactive input detected - refusing to auto-confirm an install action. Re-run interactively to confirm.");
            return false;
        }

        Console.Write(prompt);
        var answer = Console.ReadLine();
        return string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }
}
