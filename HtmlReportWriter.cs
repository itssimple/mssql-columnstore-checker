using System.Net;
using System.Text;

namespace ColumnstoreAnalyzer;

/// <summary>
/// Self-contained dashboard report (report.html) - an ADDITIONAL artifact alongside the
/// existing CSVs/JSON/markdown, aimed at the "show this to your boss" use case. Raw
/// StringBuilder generation, matching Narrative.cs/ReportWriter.cs's hand-rolled style -
/// nothing here justifies a templating dependency. Inline CSS/JS only, no CDN, so it opens
/// correctly with zero network access. Progressive disclosure uses native &lt;details&gt;
/// elements - no JavaScript required.
/// </summary>
public static class HtmlReportWriter
{
    private const int MaxRawRows = 200;

    public static void Write(string path, AnalyzerOptions opt, List<TableInfo> tables, HealthCheckResult? health)
    {
        var sb = new StringBuilder();
        AppendHead(sb, opt);
        sb.AppendLine("<body>");
        AppendHeader(sb, opt);
        AppendSummaryCards(sb, tables, health);
        AppendColumnstoreSection(sb, tables);
        if (health != null) AppendHealthSection(sb, health); else AppendHealthSkippedNotice(sb);
        AppendGlossary(sb);
        AppendFooter(sb);
        sb.AppendLine("</body></html>");

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    // ======================================================================================
    // Head / chrome
    // ======================================================================================

    private static void AppendHead(StringBuilder sb, AnalyzerOptions opt)
    {
        sb.AppendLine($$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>SQL Server Health Report - {{H(opt.Database)}}</title>
        <style>
        :root {
          --bg: #f7f8fa; --card-bg: #ffffff; --text: #1c2430; --muted: #5b6572; --border: #dde2e8;
          --accent: #2f6fed; --info: #6b7684; --low: #2f8f5b; --medium: #c9922a; --high: #d9682f; --critical: #c23b3b;
        }
        @media (prefers-color-scheme: dark) {
          :root { --bg: #14181f; --card-bg: #1c222c; --text: #e6e9ee; --muted: #9aa4b2; --border: #2c333e;
                   --accent: #6f9dff; --info: #8b95a3; --low: #4fbf85; --medium: #e0ab52; --high: #ef8a54; --critical: #e2635f; }
        }
        * { box-sizing: border-box; }
        body { margin: 0; font-family: -apple-system, Segoe UI, Roboto, Helvetica, Arial, sans-serif;
                background: var(--bg); color: var(--text); line-height: 1.45; }
        header { padding: 24px 32px; border-bottom: 1px solid var(--border); }
        header h1 { margin: 0 0 4px; font-size: 1.5rem; }
        header p { margin: 0; color: var(--muted); }
        main { max-width: 1100px; margin: 0 auto; padding: 24px 32px 64px; }
        section { margin-bottom: 32px; }
        h2 { font-size: 1.15rem; border-bottom: 1px solid var(--border); padding-bottom: 8px; }
        .cards { display: flex; flex-wrap: wrap; gap: 16px; }
        .card { background: var(--card-bg); border: 1px solid var(--border); border-radius: 10px;
                 padding: 16px 20px; min-width: 180px; flex: 1 1 200px; }
        .card .num { font-size: 1.8rem; font-weight: 700; }
        .card .label { color: var(--muted); font-size: 0.85rem; }
        table { border-collapse: collapse; width: 100%; font-size: 0.9rem; }
        table.scroll-wrap { display: block; overflow-x: auto; }
        th, td { text-align: left; padding: 6px 10px; border-bottom: 1px solid var(--border); vertical-align: top; }
        th { color: var(--muted); font-weight: 600; font-size: 0.8rem; text-transform: uppercase; }
        details { background: var(--card-bg); border: 1px solid var(--border); border-radius: 8px;
                   margin-bottom: 10px; padding: 10px 14px; }
        summary { cursor: pointer; font-weight: 600; }
        .badge { display: inline-block; padding: 2px 8px; border-radius: 999px; font-size: 0.78rem; font-weight: 600; }
        .sev-critical { background: color-mix(in srgb, var(--critical) 20%, transparent); color: var(--critical); }
        .sev-high { background: color-mix(in srgb, var(--high) 20%, transparent); color: var(--high); }
        .sev-medium { background: color-mix(in srgb, var(--medium) 20%, transparent); color: var(--medium); }
        .sev-low { background: color-mix(in srgb, var(--low) 20%, transparent); color: var(--low); }
        .sev-info { background: color-mix(in srgb, var(--info) 20%, transparent); color: var(--info); }
        .muted { color: var(--muted); }
        .pill { display: inline-block; padding: 2px 8px; border-radius: 6px; font-size: 0.78rem; border: 1px solid var(--border); }
        code { background: color-mix(in srgb, var(--border) 50%, transparent); padding: 1px 5px; border-radius: 4px; }
        footer { text-align: center; color: var(--muted); font-size: 0.8rem; padding: 24px; }
        .fill-in { border-bottom: 1px dashed var(--muted); display: inline-block; min-width: 220px; }
        </style>
        </head>
        """);
    }

    private static void AppendHeader(StringBuilder sb, AnalyzerOptions opt)
    {
        sb.AppendLine($"""
        <header>
          <h1>SQL Server Health Report</h1>
          <p>{H(opt.Server)} / {H(opt.Database)} &middot; generated {DateTime.Now:yyyy-MM-dd HH:mm}</p>
        </header>
        <main>
        """);
    }

    private static void AppendFooter(StringBuilder sb)
    {
        sb.AppendLine("""
        </main>
        <footer>Generated by ColumnstoreAnalyzer. Diagnostic snapshot only - verify high-severity findings before acting, especially in non-prod first.</footer>
        """);
    }

    // ======================================================================================
    // Executive summary
    // ======================================================================================

    private static void AppendSummaryCards(StringBuilder sb, List<TableInfo> tables, HealthCheckResult? health)
    {
        sb.AppendLine("<section class=\"cards\">");

        var strong = tables.Count(t => !t.HasColumnstore && t.CandidacyScore >= 55);
        var worthTesting = tables.Count(t => !t.HasColumnstore && t.CandidacyScore is >= 35 and < 55);
        sb.AppendLine($"""
          <div class="card"><div class="num">{strong}</div><div class="label">Strong columnstore candidates</div></div>
          <div class="card"><div class="num">{worthTesting}</div><div class="label">Worth testing</div></div>
        """);

        if (health != null)
        {
            foreach (var sev in new[] { HealthCheckSeverity.Critical, HealthCheckSeverity.High })
            {
                var count = health.Findings.Count(f => f.Severity == sev);
                sb.AppendLine($"""
                  <div class="card"><div class="num">{count}</div><div class="label">{sev} health findings</div></div>
                """);
            }

            var installed = health.Components.Count(c => c.Installed);
            sb.AppendLine($"""
              <div class="card"><div class="num">{installed}/{health.Components.Count}</div><div class="label">Community tools installed</div></div>
            """);

            var needsAnnotation = health.Findings.Count(f => f.NeedsAnnotation);
            if (needsAnnotation > 0)
                sb.AppendLine($"""
                  <div class="card"><div class="num">{needsAnnotation}</div><div class="label">Items to fill in before you go</div></div>
                """);
        }

        sb.AppendLine("</section>");
    }

    // ======================================================================================
    // Columnstore section (reuses TableInfo/ColumnStat/IndexInfo/CachedQuery already in memory)
    // ======================================================================================

    private static void AppendColumnstoreSection(StringBuilder sb, List<TableInfo> tables)
    {
        sb.AppendLine("<section><h2>Columnstore candidacy</h2>");
        if (tables.Count == 0)
        {
            sb.AppendLine("<p class=\"muted\">No candidate tables found.</p></section>");
            return;
        }

        foreach (var t in tables.OrderByDescending(x => x.CandidacyScore))
        {
            sb.AppendLine("<details>");
            sb.AppendLine($"""
              <summary>{H(t.FullName)} &mdash; score {t.CandidacyScore:0.0}
                <span class="muted">({t.RowCount:N0} rows, {t.TotalSizeMb:N0} MB{(t.HasColumnstore ? ", already columnstore" : "")})</span>
              </summary>
            """);

            if (!string.IsNullOrEmpty(t.AssessmentNotes))
                sb.AppendLine($"<p>{H(t.AssessmentNotes)}</p>");

            var topColumns = t.Columns.Where(c => c.Analyzed).OrderByDescending(c => c.DuplicationFactor).Take(8).ToList();
            if (topColumns.Count > 0)
            {
                sb.AppendLine("<table class=\"scroll-wrap\"><thead><tr><th>Column</th><th>Type</th><th>Distinct values (sampled)</th><th>Dup factor</th><th>Verdict</th></tr></thead><tbody>");
                foreach (var c in topColumns)
                    sb.AppendLine($"<tr><td>{H(c.ColumnName)}</td><td>{H(c.DataType)}</td><td>{c.DistinctValues:N0}</td><td>{c.DuplicationFactor:0.#}x</td><td>{H(c.Verdict)}</td></tr>");
                sb.AppendLine("</tbody></table>");
            }

            var flaggedIndexes = t.Indexes.Where(i => !string.IsNullOrEmpty(i.Note)).ToList();
            if (flaggedIndexes.Count > 0)
            {
                sb.AppendLine("<table class=\"scroll-wrap\"><thead><tr><th>Index</th><th>Size (MB)</th><th>Note</th></tr></thead><tbody>");
                foreach (var i in flaggedIndexes)
                    sb.AppendLine($"<tr><td>{H(i.IndexName)}</td><td>{i.SizeMb:N0}</td><td>{H(i.Note)}</td></tr>");
                sb.AppendLine("</tbody></table>");
            }

            sb.AppendLine("</details>");
        }

        sb.AppendLine("</section>");
    }

    // ======================================================================================
    // Health check section
    // ======================================================================================

    private static void AppendHealthSkippedNotice(StringBuilder sb) =>
        sb.AppendLine("<section><h2>Health check</h2><p class=\"muted\">Not run for this report - pass <code>--health-check</code> to include it.</p></section>");

    private static void AppendHealthSection(StringBuilder sb, HealthCheckResult health)
    {
        sb.AppendLine("<section><h2>Community tool coverage</h2><table class=\"scroll-wrap\"><thead><tr><th>Tool</th><th>Status</th><th>Notes</th></tr></thead><tbody>");
        foreach (var c in health.Components)
        {
            var status = c.Installed ? $"<span class=\"pill\">installed{(c.Version != null ? $" ({H(c.Version)})" : "")}</span>" : "<span class=\"pill\">not installed</span>";
            sb.AppendLine($"<tr><td>{H(c.Name)}</td><td>{status}</td><td>{H(c.InstallInstructions)}</td></tr>");
        }
        sb.AppendLine("</tbody></table></section>");

        if (health.MaintenanceJobs.Count > 0)
        {
            sb.AppendLine("<section><h2>Ola Hallengren maintenance jobs</h2><table class=\"scroll-wrap\"><thead><tr><th>Pattern</th><th>Job</th><th>Enabled</th><th>Last run</th><th>Next run</th></tr></thead><tbody>");
            foreach (var j in health.MaintenanceJobs)
                sb.AppendLine($"<tr><td>{H(j.JobNamePattern)}</td><td>{(j.JobName != null ? H(j.JobName) : "<span class=\"muted\">not found</span>")}</td>" +
                              $"<td>{(j.Found ? (j.Enabled ? "yes" : "no") : "-")}</td>" +
                              $"<td>{(j.LastRunTime != null ? $"{j.LastRunTime:yyyy-MM-dd HH:mm} ({H(j.LastRunOutcome)})" : "-")}</td>" +
                              $"<td>{(j.NextRunTime != null ? j.NextRunTime.Value.ToString("yyyy-MM-dd HH:mm") : "-")}</td></tr>");
            sb.AppendLine("</tbody></table></section>");
        }

        AppendFindingsByCategory(sb, health);
        AppendRawResultSets(sb, health);
        AppendTribalKnowledgeCapture(sb, health);
        AppendInstallActions(sb, health);
    }

    private static void AppendFindingsByCategory(StringBuilder sb, HealthCheckResult health)
    {
        sb.AppendLine("<section><h2>Findings</h2>");
        foreach (var group in health.Findings.GroupBy(f => f.Category).OrderBy(g => g.Key))
        {
            sb.AppendLine("<details>");
            var worstSeverity = group.Max(f => f.Severity); // enum: Info=0 ... Critical=4, so Max = most severe
            sb.AppendLine($"<summary>{H(group.Key)} {Badge(worstSeverity)} <span class=\"muted\">({group.Count()})</span></summary>");
            sb.AppendLine("<table class=\"scroll-wrap\"><thead><tr><th>Severity</th><th>Title</th><th>Details</th><th>Recommendation</th></tr></thead><tbody>");
            foreach (var f in group.OrderByDescending(f => f.Severity))
                sb.AppendLine($"<tr><td>{Badge(f.Severity)}</td><td>{H(f.Title)}</td><td>{H(f.Details)}</td><td>{H(f.Recommendation)}</td></tr>");
            sb.AppendLine("</tbody></table></details>");
        }
        sb.AppendLine("</section>");
    }

    private static void AppendRawResultSets(StringBuilder sb, HealthCheckResult health)
    {
        if (health.RawResultSets.Count == 0) return;

        sb.AppendLine("<section><h2>Raw diagnostic output</h2>");
        foreach (var rs in health.RawResultSets)
        {
            sb.AppendLine($"<details><summary>{H(rs.Name)} <span class=\"muted\">({rs.Rows.Count} row(s))</span></summary>");
            var shown = rs.Rows.Take(MaxRawRows).ToList();
            sb.AppendLine("<table class=\"scroll-wrap\"><thead><tr>");
            foreach (var col in rs.ColumnNames) sb.AppendLine($"<th>{H(col)}</th>");
            sb.AppendLine("</tr></thead><tbody>");
            foreach (var row in shown)
            {
                sb.AppendLine("<tr>");
                foreach (var col in rs.ColumnNames) sb.AppendLine($"<td>{H(row.GetValueOrDefault(col)?.ToString())}</td>");
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</tbody></table>");
            if (rs.Rows.Count > MaxRawRows)
                sb.AppendLine($"<p class=\"muted\">Showing first {MaxRawRows} of {rs.Rows.Count} rows - see the CSV/JSON output for the full set.</p>");
            sb.AppendLine("</details>");
        }
        sb.AppendLine("</section>");
    }

    private static void AppendTribalKnowledgeCapture(StringBuilder sb, HealthCheckResult health)
    {
        var items = health.Findings.Where(f => f.NeedsAnnotation).ToList();
        if (items.Count == 0) return;

        sb.AppendLine("<section><h2>Fill in before you go</h2>");
        sb.AppendLine("<p class=\"muted\">These are gaps only the departing owner can close - print this page and annotate, or copy into a wiki page.</p>");
        sb.AppendLine("<table class=\"scroll-wrap\"><thead><tr><th>Item</th><th>Question</th><th>Answer</th></tr></thead><tbody>");
        foreach (var f in items)
            sb.AppendLine($"<tr><td>{H(f.Title)}</td><td>{H(f.AnswerPlaceholder)}</td><td><span class=\"fill-in\">&nbsp;</span></td></tr>");
        sb.AppendLine("</tbody></table></section>");
    }

    private static void AppendInstallActions(StringBuilder sb, HealthCheckResult health)
    {
        if (health.InstallActions.Count == 0) return;

        sb.AppendLine("<section><h2>Install actions taken this run</h2>");
        sb.AppendLine("<table class=\"scroll-wrap\"><thead><tr><th>Component</th><th>Version</th><th>When</th><th>Outcome</th><th>Detail</th></tr></thead><tbody>");
        foreach (var a in health.InstallActions)
            sb.AppendLine($"<tr><td>{H(a.Component)}</td><td>{H(a.PinnedVersion)}</td><td>{a.When:yyyy-MM-dd HH:mm}</td>" +
                          $"<td>{(a.Succeeded ? "succeeded" : "did not proceed")}</td><td>{H(a.Detail)}</td></tr>");
        sb.AppendLine("</tbody></table></section>");
    }

    private static string Badge(HealthCheckSeverity sev)
    {
        var css = sev switch
        {
            HealthCheckSeverity.Critical => "sev-critical",
            HealthCheckSeverity.High => "sev-high",
            HealthCheckSeverity.Medium => "sev-medium",
            HealthCheckSeverity.Low => "sev-low",
            _ => "sev-info"
        };
        return $"<span class=\"badge {css}\">{sev}</span>";
    }

    // ======================================================================================
    // Glossary - plain-English appendix for a non-DBA audience
    // ======================================================================================

    private static readonly (string Term, string Definition)[] GlossaryTerms =
    [
        ("Columnstore index", "An index storage format that groups data by column instead of by row, enabling heavy compression and fast aggregate queries - the subject of the first half of this report."),
        ("Rowgroup", "A columnstore's storage unit, up to ~1M rows. Tables need many millions of rows before columnstore forms enough full rowgroups to pay off."),
        ("Dictionary pressure", "When a column has too many unique wide values, columnstore's compression dictionary balloons and rowgroups get trimmed, hurting compression."),
        ("Distinct ratio / duplication factor", "How repetitive a column's values are. Low distinct ratio (or high duplication factor) means the data compresses well."),
        ("MAXDOP", "Max Degree Of Parallelism - how many CPU cores a single query is allowed to use at once."),
        ("Cost threshold for parallelism", "The estimated-cost cutoff below which SQL Server runs a query on a single core instead of parallelizing it."),
        ("Wait stats", "Cumulative time SQL Server has spent waiting on various resources (disk, locks, CPU) since the last restart - a primary performance-troubleshooting signal."),
        ("CHECKDB", "DBCC CHECKDB - SQL Server's built-in database corruption checker. Should run on a regular schedule; this report flags databases with no recent successful run."),
        ("tempdb", "A shared system database used for temporary work by every database on the instance - misconfiguration here affects everything."),
        ("Orphaned user", "A database user with no matching server-level login - usually left behind after a login was dropped or the database was restored elsewhere."),
        ("Linked server", "A connection to another SQL Server (or other data source) configured inside this instance, usable from queries here."),
        ("Availability Group / AG", "A high-availability feature that keeps synchronized copies of databases on multiple servers."),
        ("CDC (Change Data Capture)", "A feature that records row-level changes to a table so other systems can consume them."),
        ("Recovery model (FULL/SIMPLE)", "Controls whether the transaction log can be backed up for point-in-time recovery (FULL) or is truncated automatically (SIMPLE)."),
        ("sysadmin", "The SQL Server server-level role with unrestricted access to everything on the instance."),
        ("First Responder Kit / sp_Blitz", "A free, widely-used set of diagnostic stored procedures by Brent Ozar Unlimited for SQL Server health checks."),
    ];

    private static void AppendGlossary(StringBuilder sb)
    {
        sb.AppendLine("<section><h2>Glossary</h2><table class=\"scroll-wrap\"><tbody>");
        foreach (var (term, def) in GlossaryTerms)
            sb.AppendLine($"<tr><td style=\"white-space:nowrap;font-weight:600\">{H(term)}</td><td class=\"muted\">{H(def)}</td></tr>");
        sb.AppendLine("</tbody></table></section>");
    }

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
}
