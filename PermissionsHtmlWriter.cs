using System.Net;
using System.Text;

namespace ColumnstoreAnalyzer;

/// <summary>
/// Self-contained dashboard for --permissions-report (permissions_report.html) - a separate file
/// from report.html, since this mode runs standalone instead of the columnstore/health-check
/// pipeline. Same conventions as HtmlReportWriter.cs (raw StringBuilder, inline CSS/JS only, no
/// CDN, WebUtility.HtmlEncode for anything embedded, native &lt;details&gt; for progressive
/// disclosure) - the CSS block is duplicated rather than shared, since these are two independent
/// standalone report modes with no other coupling.
/// </summary>
public static class PermissionsHtmlWriter
{
    private const int MaxGrantRowsPerDatabase = 200;

    public static void Write(string path, AnalyzerOptions opt, PermissionsReportResult result)
    {
        var sb = new StringBuilder();
        AppendHead(sb, opt);
        sb.AppendLine("<body>");
        AppendHeader(sb, opt, result);
        AppendSummaryCards(sb, result);
        AppendFindings(sb, result);
        AppendPerDatabaseDetail(sb, result);
        if (result.Warnings.Count > 0) AppendWarnings(sb, result);
        AppendFooter(sb);
        sb.AppendLine("</body></html>");

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void AppendHead(StringBuilder sb, AnalyzerOptions opt)
    {
        sb.AppendLine($$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Permissions Inventory - {{H(opt.Server)}}</title>
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
        main { max-width: 1600px; margin: 0 auto; padding: 24px 32px 64px; }
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
        footer { text-align: center; color: var(--muted); font-size: 0.8rem; padding: 24px; }
        </style>
        </head>
        """);
    }

    private static void AppendHeader(StringBuilder sb, AnalyzerOptions opt, PermissionsReportResult result)
    {
        sb.AppendLine($"""
        <header>
          <h1>Permissions Inventory</h1>
          <p>{H(opt.Server)} &middot; generated {result.GeneratedAt:yyyy-MM-dd HH:mm}</p>
        </header>
        <main>
        """);
    }

    private static void AppendFooter(StringBuilder sb)
    {
        sb.AppendLine("""
        </main>
        <footer>Generated by ColumnstoreAnalyzer --permissions-report. Snapshot only - re-run after making access changes to confirm.</footer>
        """);
    }

    private static void AppendSummaryCards(StringBuilder sb, PermissionsReportResult result)
    {
        var sysadminCount = result.ServerPrincipals.Count(p => p.ServerRoles.Contains("sysadmin", StringComparer.OrdinalIgnoreCase));
        var disabledCount = result.ServerPrincipals.Count(p => p.IsDisabled);
        var orphanedCount = result.DatabaseUsers.Count(u => u.IsOrphaned);
        var dbCount = result.DatabaseUsers.Select(u => u.DatabaseName).Distinct().Count();

        sb.AppendLine("<section class=\"cards\">");
        sb.AppendLine($"""
          <div class="card"><div class="num">{result.ServerPrincipals.Count}</div><div class="label">Server logins ({disabledCount} disabled)</div></div>
          <div class="card"><div class="num">{sysadminCount}</div><div class="label">sysadmin members</div></div>
          <div class="card"><div class="num">{dbCount}</div><div class="label">Databases scanned</div></div>
          <div class="card"><div class="num">{orphanedCount}</div><div class="label">Orphaned users</div></div>
          <div class="card"><div class="num">{result.ObjectPermissions.Count}</div><div class="label">Explicit object/schema/db grants</div></div>
          <div class="card"><div class="num">{result.Findings.Count}</div><div class="label">Findings flagged</div></div>
        """);
        sb.AppendLine("</section>");
    }

    private static void AppendFindings(StringBuilder sb, PermissionsReportResult result)
    {
        if (result.Findings.Count == 0) return;

        sb.AppendLine("<section><h2>Findings</h2>");
        foreach (var group in result.Findings.GroupBy(f => f.Category).OrderBy(g => g.Key))
        {
            var worst = group.Max(f => f.Severity);
            sb.AppendLine("<details>");
            sb.AppendLine($"<summary>{H(group.Key)} {Badge(worst)} <span class=\"muted\">({group.Count()})</span></summary>");
            sb.AppendLine("<table class=\"scroll-wrap\"><thead><tr><th>Severity</th><th>Title</th><th>Database</th><th>Details</th><th>Recommendation</th></tr></thead><tbody>");
            foreach (var f in group.OrderByDescending(f => f.Severity))
                sb.AppendLine($"<tr><td>{Badge(f.Severity)}</td><td>{H(f.Title)}</td><td>{H(f.DatabaseName)}</td><td>{H(f.Details)}</td><td>{H(f.Recommendation)}</td></tr>");
            sb.AppendLine("</tbody></table></details>");
        }
        sb.AppendLine("</section>");
    }

    private static void AppendPerDatabaseDetail(StringBuilder sb, PermissionsReportResult result)
    {
        sb.AppendLine("<section><h2>Per-database detail</h2>");
        foreach (var db in result.DatabaseUsers.Select(u => u.DatabaseName).Distinct().OrderBy(d => d))
        {
            var users = result.DatabaseUsers.Where(u => u.DatabaseName == db).OrderBy(u => u.UserName).ToList();
            var grants = result.ObjectPermissions.Where(p => p.DatabaseName == db).ToList();

            sb.AppendLine("<details>");
            sb.AppendLine($"<summary>{H(db)} <span class=\"muted\">({users.Count} user(s), {grants.Count} explicit grant(s))</span></summary>");

            sb.AppendLine("<table class=\"scroll-wrap\"><thead><tr><th>User</th><th>Type</th><th>Login</th><th>Roles</th><th>Orphaned</th></tr></thead><tbody>");
            foreach (var u in users)
                sb.AppendLine($"<tr><td>{H(u.UserName)}</td><td>{H(u.TypeDesc)}</td><td>{H(u.LoginName)}</td>" +
                              $"<td>{H(string.Join(", ", u.DatabaseRoles))}</td><td>{(u.IsOrphaned ? "<span class=\"pill\">orphaned</span>" : "")}</td></tr>");
            sb.AppendLine("</tbody></table>");

            if (grants.Count > 0)
            {
                // Grouped by (grantee, type, scope, permission, state) - a permission granted once per
                // column (e.g. "public" given SELECT on 40 individual columns of one table) would
                // otherwise render as 40 near-identical rows. One row per group, with a target count
                // and a capped list of what it actually applies to; full raw detail stays in the CSV.
                var grouped = PermissionGrouping.Group(grants).OrderByDescending(g => g.Targets.Count).ToList();

                sb.AppendLine("<table class=\"scroll-wrap\"><thead><tr><th>Grantee</th><th>Type</th><th>Scope</th><th>Permission</th><th>State</th><th>Targets</th></tr></thead><tbody>");
                foreach (var g in grouped.Take(MaxGrantRowsPerDatabase))
                {
                    var targetCell = g.Targets.Count <= 5
                        ? H(string.Join(", ", g.Targets))
                        : $"{g.Targets.Count} targets: {H(PermissionGrouping.TargetSummary(g.Targets, 5))} (see CSV)";
                    sb.AppendLine($"<tr><td>{H(g.GranteeName)}</td><td>{H(g.GranteeType)}</td><td>{H(g.ClassDesc)}</td>" +
                                  $"<td>{H(g.PermissionName)}</td><td>{H(g.StateDesc)}</td><td>{targetCell}</td></tr>");
                }
                sb.AppendLine("</tbody></table>");
                if (grouped.Count > MaxGrantRowsPerDatabase)
                    sb.AppendLine($"<p class=\"muted\">Showing first {MaxGrantRowsPerDatabase} of {grouped.Count} grant group(s) - see the CSV/JSON output for the full, ungrouped detail ({grants.Count} raw row(s)).</p>");
            }

            sb.AppendLine("</details>");
        }
        sb.AppendLine("</section>");
    }

    private static void AppendWarnings(StringBuilder sb, PermissionsReportResult result)
    {
        sb.AppendLine("<section><h2>Warnings (skipped)</h2><ul>");
        foreach (var w in result.Warnings)
            sb.AppendLine($"<li>{H(w)}</li>");
        sb.AppendLine("</ul></section>");
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

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
}
