using System.Text;
using System.Text.Json;

namespace ColumnstoreAnalyzer;

/// <summary>Writes --self-test results to JSON (precise, for handing back for review) and Markdown
/// (human-skimmable) - the two output formats for this mode, distinct from the other three report
/// types since this is a diagnostic pass/fail run, not a candidacy/inventory report.</summary>
public static class SelfTestWriter
{
    public static void WriteAll(AnalyzerOptions opt, SelfTestResult result)
    {
        Directory.CreateDirectory(opt.OutputFolder);

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(opt.OutputFolder, "self_test_results.json"), json, Encoding.UTF8);
        File.WriteAllText(Path.Combine(opt.OutputFolder, "self_test_results.md"), BuildMarkdown(opt, result), Encoding.UTF8);

        Console.WriteLine();
        Console.WriteLine($"=== SELF-TEST SUMMARY: {result.PassCount} passed, {result.FailCount} failed (of {result.Steps.Count}) ===");
        if (result.FailCount > 0)
        {
            Console.WriteLine("Failed steps:");
            foreach (var s in result.Steps.Where(s => !s.Success))
                Console.WriteLine($"  - [{s.Category}] {s.Step}: {s.ErrorType} - {s.ErrorMessage}");
        }
    }

    private static string BuildMarkdown(AnalyzerOptions opt, SelfTestResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Self-Test Results — {opt.Server}");
        sb.AppendLine("_Read-only diagnostic run - every step here is a SELECT, a read-only DBCC command, or an EXEC " +
                      "of an already-installed read-only diagnostic proc. Nothing wrote to the instance._");
        sb.AppendLine($"_Generated {result.GeneratedAt:yyyy-MM-dd HH:mm}._");
        sb.AppendLine();
        sb.AppendLine($"**{result.PassCount} passed, {result.FailCount} failed** (of {result.Steps.Count} steps)");
        sb.AppendLine();

        foreach (var category in result.Steps.Select(s => s.Category).Distinct())
        {
            sb.AppendLine($"## {category}");
            sb.AppendLine();
            sb.AppendLine("| Step | Result | Elapsed | Detail |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var s in result.Steps.Where(s => s.Category == category))
            {
                var res = s.Success ? "OK" : "**FAILED**";
                var detail = s.Success ? s.Summary : $"{s.ErrorType}: {s.ErrorMessage}";
                sb.AppendLine($"| {s.Step} | {res} | {s.ElapsedMs}ms | {detail} |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
