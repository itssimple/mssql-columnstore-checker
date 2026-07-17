using System.Text;
using System.Text.Json;

namespace ColumnstoreAnalyzer;

/// <summary>
/// Optional: sends a compact version of the analysis to any OpenAI-compatible
/// chat-completions endpoint (Ollama with `ollama serve`, LM Studio, llama.cpp server,
/// vLLM, or an actual OpenAI-compatible gateway) and returns a narrative write-up.
/// Entirely optional — the rule-based Narrative class needs no AI at all.
/// </summary>
public static class LlmClient
{
    public static string GenerateNarrative(AnalyzerOptions opt, List<TableInfo> tables, string ruleBasedReport)
    {
        var payload = BuildCompactPayload(tables);

        var systemPrompt =
            "You are a senior SQL Server DBA reviewing a columnstore-candidacy analysis. " +
            "Write a concise, direct narrative in markdown: a verdict per table with the evidence " +
            "(data repetitiveness, workload shape, index findings), call out dead or pathological indexes, " +
            "note columnstore blockers (computed columns, LOBs, update-heavy writes), and end with a " +
            "prioritized punch list with rough MB estimates. Be honest about caveats (usage stats reset on " +
            "restart, plan-cache visibility). Do not invent numbers not present in the data.";

        var userPrompt =
            "Here is the machine-readable analysis data:\n\n```json\n" + payload + "\n```\n\n" +
            "A rule-based pre-analysis produced the following findings — use them as grounding, " +
            "correct or extend where the data supports it:\n\n" + ruleBasedReport;

        var requestBody = JsonSerializer.Serialize(new
        {
            model = opt.LlmModel,
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        });

        var endpoint = opt.LlmEndpoint!.TrimEnd('/');
        if (!endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            endpoint += "/v1/chat/completions";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(opt.LlmTimeoutSeconds) };
        if (!string.IsNullOrEmpty(opt.LlmApiKey))
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opt.LlmApiKey);

        using var response = http.PostAsync(endpoint,
            new StringContent(requestBody, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();

        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"LLM endpoint returned {(int)response.StatusCode}: {Truncate(body, 500)}");

        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return content ?? throw new InvalidOperationException("LLM endpoint returned an empty message.");
    }

    /// <summary>Compact JSON: table stats, analyzed columns, indexes, top queries trimmed.</summary>
    private static string BuildCompactPayload(List<TableInfo> tables)
    {
        var compact = tables.Select(t => new
        {
            table = t.FullName,
            score = t.CandidacyScore,
            rows = t.RowCount,
            totalMb = t.TotalSizeMb,
            baseDataMb = t.BaseDataMb,
            ncIndexMb = t.NonclusteredMb,
            hasColumnstore = t.HasColumnstore,
            scanPct = Math.Round(t.ScanPct, 1),
            writePct = Math.Round(t.WritePct, 1),
            updateShareOfWritesPct = Math.Round(t.UpdateShareOfWritesPct, 1),
            seeks = t.UserSeeks,
            scans = t.UserScans,
            lookups = t.UserLookups,
            writes = t.UserUpdates,
            leafInserts = t.LeafInserts,
            leafUpdates = t.LeafUpdates,
            leafDeletes = t.LeafDeletes,
            ioLatchWaitMs = t.PageIoLatchWaitMs,
            notes = t.AssessmentNotes,
            columns = t.Columns.Select(c => new
            {
                name = c.ColumnName,
                type = c.DataType,
                analyzed = c.Analyzed,
                distinct = c.DistinctValues,
                duplicationFactor = Math.Round(c.DuplicationFactor, 1),
                nulls = c.NullCount,
                avgBytes = Math.Round(c.AvgByteLength, 1),
                computed = c.IsComputed,
                lob = c.IsLob,
                verdict = c.Analyzed ? c.Verdict : c.SkipReason
            }),
            indexes = t.Indexes.Select(i => new
            {
                name = i.IndexName,
                type = i.TypeDesc,
                sizeMb = i.SizeMb,
                keys = i.KeyColumns,
                includes = i.IncludedColumns,
                reads = i.UserSeeks + i.UserScans + i.UserLookups,
                writes = i.UserUpdates,
                note = i.Note
            }),
            topQueries = t.ReferencingQueries
                .Where(q => !q.StatementText.Contains("AS __rows", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(q => q.TotalLogicalReads)
                .Take(5)
                .Select(q => new
                {
                    execs = q.ExecutionCount,
                    reads = q.TotalLogicalReads,
                    cpuMs = Math.Round(q.TotalCpuMs),
                    text = Truncate(q.StatementText, 300)
                })
        });

        return JsonSerializer.Serialize(compact, new JsonSerializerOptions { WriteIndented = false });
    }

    private static string Truncate(string s, int max)
    {
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..max] + "...";
    }
}
