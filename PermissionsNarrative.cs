using System.Text;

namespace ColumnstoreAnalyzer;

/// <summary>
/// Markdown narrative for --permissions-report. Exec/summary level, not a full row dump - the
/// full row-by-row detail belongs in the CSVs/JSON, this is deliberately kept scoped to counts,
/// flagged findings, and a per-database summary table, matching Narrative.cs's role for the
/// columnstore report (kept as a separate file rather than extending Narrative.cs, since that one
/// is intentionally scoped to columnstore prose only).
/// </summary>
public static class PermissionsNarrative
{
    public static string Build(AnalyzerOptions opt, PermissionsReportResult result)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# Permissions Inventory — {opt.Server}");
        sb.AppendLine($"_Generated {result.GeneratedAt:yyyy-MM-dd HH:mm}. Full row-by-row detail is in the CSVs/JSON in this folder._");
        sb.AppendLine();

        var sysadmins = result.ServerPrincipals.Where(p => p.ServerRoles.Contains("sysadmin", StringComparer.OrdinalIgnoreCase)).ToList();
        var disabledLingering = result.ServerPrincipals.Where(p => p.IsDisabled && p.DatabaseUserCount > 0).ToList();
        var orphaned = result.DatabaseUsers.Where(u => u.IsOrphaned).ToList();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine($"- **{result.ServerPrincipals.Count}** server login(s) ({result.ServerPrincipals.Count(p => p.IsDisabled)} disabled)");
        sb.AppendLine($"- **{sysadmins.Count}** sysadmin member(s)");
        sb.AppendLine($"- **{result.DatabaseUsers.Select(u => u.DatabaseName).Distinct().Count()}** database(s) scanned, **{result.DatabaseUsers.Count}** user mapping(s), **{orphaned.Count}** orphaned");
        sb.AppendLine($"- **{result.ObjectPermissions.Count}** explicit database/schema/object grant(s)");
        if (result.Warnings.Count > 0)
            sb.AppendLine($"- **{result.Warnings.Count}** database(s)/step(s) skipped (see Warnings below)");
        sb.AppendLine();

        if (sysadmins.Count > 0)
        {
            sb.AppendLine("## sysadmin members");
            sb.AppendLine();
            foreach (var p in sysadmins)
                sb.AppendLine($"- `{p.Name}` ({p.TypeDesc}){(p.IsDisabled ? " - **disabled**" : "")}");
            sb.AppendLine();
        }

        if (disabledLingering.Count > 0)
        {
            sb.AppendLine("## Disabled logins still lingering with live access");
            sb.AppendLine();
            foreach (var p in disabledLingering)
                sb.AppendLine($"- `{p.Name}` - still mapped to a database user in **{p.DatabaseUserCount}** database(s)");
            sb.AppendLine();
        }

        if (orphaned.Count > 0)
        {
            sb.AppendLine("## Orphaned database users");
            sb.AppendLine();
            foreach (var u in orphaned.OrderBy(u => u.DatabaseName).ThenBy(u => u.UserName))
                sb.AppendLine($"- `{u.DatabaseName}`.`{u.UserName}` ({u.TypeDesc})");
            sb.AppendLine();
        }

        var highRisk = result.Findings.Where(f => f.Category == "Landmine" && f.Severity >= HealthCheckSeverity.Medium).ToList();
        if (highRisk.Count > 0)
        {
            sb.AppendLine("## High-risk grants");
            sb.AppendLine();
            foreach (var f in highRisk.OrderByDescending(f => f.Severity))
                sb.AppendLine($"- **{f.Severity}** — {f.Title} ({f.DatabaseName})");
            sb.AppendLine();
        }

        sb.AppendLine("## Per-database summary");
        sb.AppendLine();
        sb.AppendLine("| Database | Users | Orphaned | Explicit grants |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var db in result.DatabaseUsers.Select(u => u.DatabaseName).Distinct().OrderBy(d => d))
        {
            var users = result.DatabaseUsers.Where(u => u.DatabaseName == db).ToList();
            var grants = result.ObjectPermissions.Count(p => p.DatabaseName == db);
            sb.AppendLine($"| {db} | {users.Count} | {users.Count(u => u.IsOrphaned)} | {grants} |");
        }
        sb.AppendLine();

        if (result.Warnings.Count > 0)
        {
            sb.AppendLine("## Warnings");
            sb.AppendLine();
            foreach (var w in result.Warnings)
                sb.AppendLine($"- {w}");
        }

        return sb.ToString();
    }
}
