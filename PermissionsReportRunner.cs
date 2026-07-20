namespace ColumnstoreAnalyzer;

/// <summary>
/// Orchestrates the standalone --permissions-report mode: server principals/roles, then every
/// online user database's users/roles/explicit permissions. Same non-fatal-by-design convention
/// as the rest of the tool - a database the connecting login can't see into (missing VIEW
/// DEFINITION or similar) is skipped with a warning, not a fatal error.
/// </summary>
public static class PermissionsReportRunner
{
    public static PermissionsReportResult Run(AnalyzerOptions opt)
    {
        var analyzer = new PermissionsAnalyzer(opt);
        var result = new PermissionsReportResult { ServerName = opt.Server, GeneratedAt = DateTime.Now };

        var loginsBySid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Console.Write("  - server principals & roles ... ");
        try
        {
            result.ServerPrincipals.AddRange(analyzer.GetServerPrincipals());
            loginsBySid = analyzer.GetServerLoginsBySid();
            Console.WriteLine($"ok - {result.ServerPrincipals.Count} principal(s)");
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED (non-fatal): " + ex.Message);
            result.Warnings.Add("Failed to enumerate server principals: " + ex.Message);
        }

        List<string> databases;
        Console.Write("  - discovering databases ... ");
        try
        {
            databases = analyzer.ListDatabases();
            Console.WriteLine($"ok - {databases.Count} database(s)");
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAILED: " + ex.Message);
            result.Warnings.Add("Failed to enumerate databases: " + ex.Message);
            return result;
        }

        foreach (var db in databases)
        {
            Console.Write($"  - {db} ... ");
            try
            {
                var users = analyzer.GetDatabaseUsers(db, loginsBySid);
                var perms = analyzer.GetObjectPermissions(db);
                result.DatabaseUsers.AddRange(users);
                result.ObjectPermissions.AddRange(perms);
                Console.WriteLine($"ok - {users.Count} user(s), {perms.Count} explicit permission(s)");
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED (non-fatal, likely insufficient permission): " + ex.Message);
                result.Warnings.Add($"{db}: {ex.Message}");
            }
        }

        ComputeFindings(result);
        return result;
    }

    private static void ComputeFindings(PermissionsReportResult result)
    {
        var dbUserCounts = result.DatabaseUsers
            .Where(u => u.LoginName != null)
            .GroupBy(u => u.LoginName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var p in result.ServerPrincipals)
        {
            if (dbUserCounts.TryGetValue(p.Name, out var count)) p.DatabaseUserCount = count;

            if (p.ServerRoles.Contains("sysadmin", StringComparer.OrdinalIgnoreCase))
                result.Findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Security", Severity = HealthCheckSeverity.Info,
                    Title = $"sysadmin member: {p.Name}{(p.IsDisabled ? " (disabled)" : "")}",
                    Details = $"Login type: {p.TypeDesc}.", ObjectName = p.Name
                });

            if (p.IsDisabled && p.DatabaseUserCount > 0)
                result.Findings.Add(new HealthCheckFinding
                {
                    Source = "Native", Category = "Landmine", Severity = HealthCheckSeverity.High,
                    Title = $"Disabled login \"{p.Name}\" still has live database access",
                    Details = $"Mapped to a database user in {p.DatabaseUserCount} database(s) despite being disabled at the server level.",
                    Recommendation = "Drop the orphaned database user mappings, or re-enable the login if access is still intended.",
                    ObjectName = p.Name, NeedsAnnotation = true,
                    AnswerPlaceholder = "Should this account be fully removed, or does something still depend on it?"
                });
        }

        foreach (var u in result.DatabaseUsers.Where(u => u.IsOrphaned))
            result.Findings.Add(new HealthCheckFinding
            {
                Source = "Native", Category = "Security", Severity = HealthCheckSeverity.Low,
                Title = $"Orphaned database user: {u.UserName}",
                Details = $"No matching server-level login for this {u.TypeDesc} user.",
                Recommendation = "Drop the user or re-map it with ALTER USER ... WITH LOGIN.",
                DatabaseName = u.DatabaseName, ObjectName = u.UserName
            });

        string[] highRiskPermissions = ["CONTROL", "ALTER", "TAKE OWNERSHIP"];
        foreach (var perm in result.ObjectPermissions)
        {
            var isHighRiskPermission = perm.StateDesc == "GRANT" &&
                                       highRiskPermissions.Contains(perm.PermissionName, StringComparer.OrdinalIgnoreCase);
            var isPublicGrant = perm.GranteeName.Equals("public", StringComparison.OrdinalIgnoreCase);
            if (!isHighRiskPermission && !isPublicGrant) continue;

            var target = perm.ObjectName != null ? $"{perm.SchemaName}.{perm.ObjectName}"
                : perm.SchemaName ?? "(database-level)";
            result.Findings.Add(new HealthCheckFinding
            {
                Source = "Native", Category = "Landmine",
                Severity = isPublicGrant ? HealthCheckSeverity.High : HealthCheckSeverity.Medium,
                Title = $"{perm.StateDesc} {perm.PermissionName} on {target} to {perm.GranteeName}",
                Details = $"Grantee type: {perm.GranteeType}, scope: {perm.ClassDesc}.",
                Recommendation = isPublicGrant
                    ? "Explicit grants to the 'public' role apply to every user in the database - verify this is intentional."
                    : "High-impact permission (CONTROL/ALTER/TAKE OWNERSHIP) - verify this grantee still needs it.",
                DatabaseName = perm.DatabaseName, ObjectName = target
            });
        }
    }
}
