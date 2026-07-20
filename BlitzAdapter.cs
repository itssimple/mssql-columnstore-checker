namespace ColumnstoreAnalyzer;

/// <summary>
/// Pure mapper, no DB access - DynamicResultSet -&gt; HealthCheckFinding, absorbing FRK's
/// schema fragility in one place. Defensive by design: a missing/renamed column degrades
/// to a raw-row Info finding rather than throwing, since sp_Blitz's exact column set can
/// change between First Responder Kit releases.
///
/// Caveat: the Priority -&gt; severity breakpoints below are a best-effort reading of Ozar's
/// convention (lower Priority = more urgent), not verified against a live sp_Blitz run -
/// sanity-check against real output before relying on the severity buckets.
/// </summary>
internal static class BlitzAdapter
{
    private static readonly string[] PriorityColumns = ["Priority"];
    private static readonly string[] CategoryColumns = ["FindingsGroup", "Category"];
    private static readonly string[] TitleColumns = ["Finding", "CheckName"];
    private static readonly string[] DetailsColumns = ["Details", "Detail"];
    private static readonly string[] RecommendationColumns = ["URL", "HowToStopThisWarning"];
    private static readonly string[] DatabaseColumns = ["DatabaseName", "Database"];

    /// <summary>Result sets without a Priority/Finding-like column aren't discrete findings
    /// (e.g. sp_BlitzCache/sp_BlitzFirst) - callers should route those to raw display instead.</summary>
    public static bool LooksFindingShaped(DynamicResultSet rs) =>
        FindColumn(rs.ColumnNames, PriorityColumns) != null || FindColumn(rs.ColumnNames, TitleColumns) != null;

    public static List<HealthCheckFinding> ToFindings(string source, DynamicResultSet rs)
    {
        var findings = new List<HealthCheckFinding>();
        var priorityCol = FindColumn(rs.ColumnNames, PriorityColumns);
        var categoryCol = FindColumn(rs.ColumnNames, CategoryColumns);
        var titleCol = FindColumn(rs.ColumnNames, TitleColumns);
        var detailsCol = FindColumn(rs.ColumnNames, DetailsColumns);
        var recommendationCol = FindColumn(rs.ColumnNames, RecommendationColumns);
        var databaseCol = FindColumn(rs.ColumnNames, DatabaseColumns);

        foreach (var row in rs.Rows)
        {
            var dbName = databaseCol != null ? NullIfEmpty(row.GetValueOrDefault(databaseCol)?.ToString()) : null;

            if (titleCol == null && priorityCol == null)
            {
                findings.Add(RawRowFinding(source, row, dbName));
                continue;
            }

            var title = titleCol != null ? row.GetValueOrDefault(titleCol)?.ToString() : null;
            findings.Add(new HealthCheckFinding
            {
                Source = source,
                Category = categoryCol != null ? row.GetValueOrDefault(categoryCol)?.ToString() ?? "" : "",
                Severity = ToSeverity(priorityCol != null ? row.GetValueOrDefault(priorityCol) : null),
                Title = !string.IsNullOrWhiteSpace(title) ? title! : $"{source} finding",
                Details = detailsCol != null ? row.GetValueOrDefault(detailsCol)?.ToString() ?? "" : RawRowText(row),
                Recommendation = recommendationCol != null ? row.GetValueOrDefault(recommendationCol)?.ToString() : null,
                DatabaseName = dbName,
                RawColumns = row.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString())
            });
        }

        return findings;
    }

    private static HealthCheckFinding RawRowFinding(string source, Dictionary<string, object?> row, string? dbName) => new()
    {
        Source = source, Category = "Uncategorized", Severity = HealthCheckSeverity.Info,
        Title = $"{source} row (unrecognized schema)",
        Details = RawRowText(row),
        DatabaseName = dbName,
        RawColumns = row.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString())
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string RawRowText(Dictionary<string, object?> row) =>
        string.Join("; ", row.Select(kv => $"{kv.Key}={kv.Value}"));

    private static HealthCheckSeverity ToSeverity(object? priorityValue)
    {
        if (priorityValue is null || !int.TryParse(priorityValue.ToString(), out var p))
            return HealthCheckSeverity.Info;

        return p switch
        {
            <= 50 => HealthCheckSeverity.Critical,
            <= 100 => HealthCheckSeverity.High,
            <= 150 => HealthCheckSeverity.Medium,
            <= 200 => HealthCheckSeverity.Low,
            _ => HealthCheckSeverity.Info
        };
    }

    /// <summary>Case-insensitive lookup that returns the column's ACTUAL casing (needed to key into the row dictionary).</summary>
    private static string? FindColumn(List<string> columns, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var match = columns.FirstOrDefault(col => col.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return null;
    }
}
