namespace ColumnstoreAnalyzer;

/// <summary>Shared CSV field-escaping, used by every report writer (extracted so it's unit-testable
/// in one place instead of duplicated per writer).</summary>
internal static class CsvUtil
{
    public static string Escape(string? s)
    {
        s ??= "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        if (s.Contains(',') || s.Contains('"'))
            s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}
