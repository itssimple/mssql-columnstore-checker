using System.Text;
using System.Text.Json;

namespace ColumnstoreAnalyzer;

public static class ReportWriter
{
    public static void WriteAll(AnalyzerOptions opt, List<TableInfo> tables)
    {
        Directory.CreateDirectory(opt.OutputFolder);

        WriteCandidatesCsv(Path.Combine(opt.OutputFolder, "1_ranked_candidates.csv"), tables);
        WriteColumnsCsv(Path.Combine(opt.OutputFolder, "2_column_analysis.csv"), tables);
        WriteIndexesCsv(Path.Combine(opt.OutputFolder, "3_index_inventory.csv"), tables);
        WriteQueriesCsv(Path.Combine(opt.OutputFolder, "4_referencing_queries.csv"), tables);
        WriteJson(Path.Combine(opt.OutputFolder, "full_report.json"), tables);

        WriteConsoleSummary(tables);
        Console.WriteLine();
        Console.WriteLine($"Reports written to: {opt.OutputFolder}");
    }

    private static void WriteConsoleSummary(List<TableInfo> tables)
    {
        Console.WriteLine();
        Console.WriteLine("=== RANKED COLUMNSTORE CANDIDATES ===");
        Console.WriteLine($"{"Score",6}  {"Rows",14}  {"Size MB",10}  {"NC MB",10}  {"Scan%",6}  {"Write%",7}  {"MedDup",8}  Table");
        Console.WriteLine(new string('-', 110));
        foreach (var t in tables.OrderByDescending(x => x.CandidacyScore))
        {
            Console.WriteLine(
                $"{t.CandidacyScore,6:0.0}  {t.RowCount,14:N0}  {t.TotalSizeMb,10:N0}  {t.NonclusteredMb,10:N0}  " +
                $"{t.ScanPct,6:0.0}  {t.WritePct,7:0.0}  {t.MedianDuplicationFactor,8:0.#}  {t.FullName}");
            if (!string.IsNullOrEmpty(t.AssessmentNotes))
                Console.WriteLine($"        -> {t.AssessmentNotes}");
        }
        Console.WriteLine();
        Console.WriteLine("Score >= 60: strong candidate | 40-60: worth testing (maybe NC columnstore) | < 40: probably leave alone");
    }

    private static void WriteCandidatesCsv(string path, List<TableInfo> tables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("table,score,row_count,total_size_mb,base_data_mb,nonclustered_mb,nc_bloat_ratio," +
                      "potential_rowgroups,pct_low_cardinality_cols,pct_medium_cardinality_cols,median_duplication_factor," +
                      "dictionary_pressure_cols,lob_cols,scan_pct,write_pct,update_share_of_writes_pct," +
                      "seeks,scans,lookups,updates,row_lock_wait_ms,page_lock_wait_ms,page_io_latch_wait_ms," +
                      "missing_index_suggestions,sampled,sampled_rows,notes");
        foreach (var t in tables.OrderByDescending(x => x.CandidacyScore))
        {
            sb.AppendLine(string.Join(",",
                Csv(t.FullName), F(t.CandidacyScore), t.RowCount, F(t.TotalSizeMb), F(t.BaseDataMb), F(t.NonclusteredMb),
                F(t.NcIndexBloatRatio), F(t.PotentialFullRowgroups), F(t.PctLowCardinalityColumns),
                F(t.PctMediumCardinalityColumns), F(t.MedianDuplicationFactor),
                t.DictionaryPressureColumns, t.LobColumns, F(t.ScanPct), F(t.WritePct), F(t.UpdateShareOfWritesPct),
                t.UserSeeks, t.UserScans, t.UserLookups, t.UserUpdates,
                t.RowLockWaitMs, t.PageLockWaitMs, t.PageIoLatchWaitMs,
                t.MissingIndexSuggestions, t.WasSampled, t.SampledRows, Csv(t.AssessmentNotes)));
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void WriteColumnsCsv(string path, List<TableInfo> tables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("table,column,data_type,analyzed,sample_rows,distinct_values,null_count," +
                      "distinct_ratio,duplication_factor,avg_byte_length,dictionary_pressure,verdict");
        foreach (var t in tables)
        foreach (var c in t.Columns)
        {
            sb.AppendLine(string.Join(",",
                Csv(t.FullName), Csv(c.ColumnName), Csv(c.DataType), c.Analyzed,
                c.SampleRows, c.DistinctValues, c.NullCount,
                F(c.DistinctRatio, "0.####"), F(c.DuplicationFactor), F(c.AvgByteLength),
                c.DictionaryPressure, Csv(c.Verdict)));
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void WriteIndexesCsv(string path, List<TableInfo> tables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("table,index,type,size_mb,key_columns,included_columns,key_col_count,included_col_count," +
                      "seeks,scans,lookups,updates,note");
        foreach (var t in tables)
        foreach (var i in t.Indexes.OrderByDescending(x => x.SizeMb))
        {
            sb.AppendLine(string.Join(",",
                Csv(t.FullName), Csv(i.IndexName), Csv(i.TypeDesc), F(i.SizeMb),
                Csv(i.KeyColumns), Csv(i.IncludedColumns), i.KeyColumnCount, i.IncludedColumnCount,
                i.UserSeeks, i.UserScans, i.UserLookups, i.UserUpdates, Csv(i.Note)));
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void WriteQueriesCsv(string path, List<TableInfo> tables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("table,execution_count,total_logical_reads,total_cpu_ms,total_elapsed_ms,last_execution,statement_text");
        foreach (var t in tables)
        foreach (var q in t.ReferencingQueries)
        {
            var text = q.StatementText.Length > 4000 ? q.StatementText[..4000] : q.StatementText;
            sb.AppendLine(string.Join(",",
                Csv(t.FullName), q.ExecutionCount, q.TotalLogicalReads,
                F(q.TotalCpuMs), F(q.TotalElapsedMs),
                q.LastExecutionTime.ToString("yyyy-MM-dd HH:mm:ss"), Csv(text)));
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static void WriteJson(string path, List<TableInfo> tables)
    {
        var json = JsonSerializer.Serialize(
            tables.OrderByDescending(t => t.CandidacyScore),
            new JsonSerializerOptions { WriteIndented = true });
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

    private static string F(double d, string fmt = "0.##") =>
        d.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);

    private static string F(decimal d, string fmt = "0.##") =>
        d.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
}
