namespace ColumnstoreAnalyzer;

/// <summary>
/// Pure grouping logic for ObjectPermissionInfo rows, shared by PermissionsReportRunner (finding
/// generation) and PermissionsHtmlWriter (display) - extracted so both use identical logic and so
/// it's unit-testable without a database. Groups by (database, grantee, granteeType, permission,
/// state) so a permission granted once per column (e.g. "public" given SELECT on 40 individual
/// columns of one table) renders/reports as one entry with a target count, not 40 near-duplicates.
/// </summary>
internal static class PermissionGrouping
{
    public sealed record PermissionGroup(
        string DatabaseName, string GranteeName, string GranteeType, string ClassDesc, string PermissionName,
        string StateDesc, List<string> Targets);

    /// <summary>Human-readable target of a single grant: "schema.object", "schema.object.column"
    /// (column-level grants), a bare schema name (schema-level grants), or "(database-level)".</summary>
    public static string TargetLabel(ObjectPermissionInfo p)
    {
        if (p.ObjectName == null) return p.SchemaName ?? "(database-level)";
        var baseName = $"{p.SchemaName}.{p.ObjectName}";
        return p.ColumnName != null ? $"{baseName}.{p.ColumnName}" : baseName;
    }

    /// <summary>Groups by database+grantee+scope+permission+state - scope (ClassDesc) is included so a
    /// schema-level grant and an object-level grant to the same grantee/permission never get merged.</summary>
    public static List<PermissionGroup> Group(IEnumerable<ObjectPermissionInfo> permissions) =>
        permissions
            .GroupBy(p => (p.DatabaseName, p.GranteeName, p.GranteeType, p.ClassDesc, p.PermissionName, p.StateDesc))
            .Select(g => new PermissionGroup(
                g.Key.DatabaseName, g.Key.GranteeName, g.Key.GranteeType, g.Key.ClassDesc, g.Key.PermissionName, g.Key.StateDesc,
                g.Select(TargetLabel).Distinct().OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

    /// <summary>Comma-joined target list, capped with a "+N more" suffix - callers append their own
    /// contextual note (e.g. "see the CSV") after this if they want one.</summary>
    public static string TargetSummary(List<string> targets, int cap) =>
        targets.Count <= cap
            ? string.Join(", ", targets)
            : string.Join(", ", targets.Take(cap)) + $", +{targets.Count - cap} more";
}
