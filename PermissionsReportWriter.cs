using System.Text;
using System.Text.Json;

namespace ColumnstoreAnalyzer;

/// <summary>CSV + JSON output for --permissions-report. Same hand-rolled Csv()/F() style as ReportWriter.cs.</summary>
public static class PermissionsReportWriter
{
    public static void WriteAll(AnalyzerOptions opt, PermissionsReportResult result)
    {
        Directory.CreateDirectory(opt.OutputFolder);

        WriteServerPrincipalsCsv(Path.Combine(opt.OutputFolder, "permissions_server_principals.csv"), result);
        WriteDatabaseUsersCsv(Path.Combine(opt.OutputFolder, "permissions_database_users.csv"), result);
        WriteObjectGrantsCsv(Path.Combine(opt.OutputFolder, "permissions_object_grants.csv"), result);
        WriteJson(Path.Combine(opt.OutputFolder, "permissions_report.json"), result);

        WriteConsoleSummary(result);
    }

    private static void WriteConsoleSummary(PermissionsReportResult result)
    {
        Console.WriteLine();
        Console.WriteLine("=== PERMISSIONS INVENTORY SUMMARY ===");
        Console.WriteLine($"{result.ServerPrincipals.Count} server login(s), " +
                          $"{result.ServerPrincipals.Count(p => p.IsDisabled)} disabled, " +
                          $"{result.ServerPrincipals.Count(p => p.ServerRoles.Contains("sysadmin", StringComparer.OrdinalIgnoreCase))} sysadmin(s)");
        Console.WriteLine($"{result.DatabaseUsers.Count} database user mapping(s) across databases, " +
                          $"{result.DatabaseUsers.Count(u => u.IsOrphaned)} orphaned");
        Console.WriteLine($"{result.ObjectPermissions.Count} explicit database/schema/object permission(s)");
        Console.WriteLine($"{result.Findings.Count} finding(s) flagged for review");
        if (result.Warnings.Count > 0)
            Console.WriteLine($"{result.Warnings.Count} database(s)/step(s) skipped - see permissions_report.json for details");
    }

    private static void WriteServerPrincipalsCsv(string path, PermissionsReportResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("name,type,is_disabled,create_date,default_database,server_roles,database_user_count");
        foreach (var p in result.ServerPrincipals.OrderBy(p => p.Name))
            sb.AppendLine(string.Join(",",
                Csv(p.Name), Csv(p.TypeDesc), p.IsDisabled, p.CreateDate.ToString("yyyy-MM-dd HH:mm:ss"),
                Csv(p.DefaultDatabaseName), Csv(string.Join(';', p.ServerRoles)), p.DatabaseUserCount));
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void WriteDatabaseUsersCsv(string path, PermissionsReportResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("database,user,type,login,orphaned,database_roles");
        foreach (var u in result.DatabaseUsers.OrderBy(u => u.DatabaseName).ThenBy(u => u.UserName))
            sb.AppendLine(string.Join(",",
                Csv(u.DatabaseName), Csv(u.UserName), Csv(u.TypeDesc), Csv(u.LoginName),
                u.IsOrphaned, Csv(string.Join(';', u.DatabaseRoles))));
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void WriteObjectGrantsCsv(string path, PermissionsReportResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("database,grantee,grantee_type,scope,permission,state,schema,object");
        foreach (var p in result.ObjectPermissions.OrderBy(p => p.DatabaseName).ThenBy(p => p.GranteeName))
            sb.AppendLine(string.Join(",",
                Csv(p.DatabaseName), Csv(p.GranteeName), Csv(p.GranteeType), Csv(p.ClassDesc),
                Csv(p.PermissionName), Csv(p.StateDesc), Csv(p.SchemaName), Csv(p.ObjectName)));
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void WriteJson(string path, PermissionsReportResult result)
    {
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json, Encoding.UTF8);
    }

    private static string Csv(string? s)
    {
        s ??= "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        if (s.Contains(',') || s.Contains('"'))
            s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}
