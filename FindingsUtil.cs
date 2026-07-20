namespace ColumnstoreAnalyzer;

/// <summary>Pure post-processing for HealthCheckFinding lists - extracted out of HealthCheckRunner so
/// it's unit-testable without a database, and reusable by any other finding-producing pipeline
/// (PermissionsReportRunner also populates HealthCheckFinding, though it doesn't need this filter
/// since its per-database loop is already scoped at the query level).</summary>
internal static class FindingsUtil
{
    /// <summary>Drops findings scoped to a database other than the one requested, unless allDatabases
    /// is set. Findings with no DatabaseName (server-wide info) are never dropped - can't tell their
    /// scope, so default to keeping them.</summary>
    public static void FilterToDatabase(List<HealthCheckFinding> findings, string database, bool allDatabases)
    {
        if (allDatabases) return;
        findings.RemoveAll(f => f.DatabaseName != null && !f.DatabaseName.Equals(database, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Removes exact-duplicate findings, keeping the first occurrence of each.</summary>
    public static void Deduplicate(List<HealthCheckFinding> findings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        findings.RemoveAll(f => !seen.Add(DedupeKey(f)));
    }

    private static string DedupeKey(HealthCheckFinding f) =>
        $"{f.Source}{f.Category}{f.Severity}{f.Title}{f.Details}{f.DatabaseName}{f.ObjectName}";
}
