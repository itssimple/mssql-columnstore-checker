namespace ColumnstoreAnalyzer;

/// <summary>
/// Candidacy score (0-100). A triage heuristic, not gospel. The weighting follows the
/// standard columnstore guidance popularized by Brent Ozar and the SQL Server docs:
///
///  * Columnstore compression = dictionary + run-length encoding, so REPETITIVE data
///    (low distinct ratio per column) is where the magic happens. This is the biggest factor.
///  * Size matters: rowgroups hold up to 1,048,576 rows. A table needs many millions of rows
///    before columnstore forms enough full rowgroups to be worth it.
///  * Scan-heavy analytic access patterns benefit; seek-heavy OLTP does not.
///  * UPDATEs are the worst operation (delete-bitmap + delta-store churn -> fragmentation).
///  * High-cardinality wide strings cause dictionary pressure -> trimmed rowgroups.
/// </summary>
public static class Scoring
{
    public static void Score(TableInfo t)
    {
        if (t.HasColumnstore)
        {
            t.CandidacyScore = 0;
            t.AssessmentNotes = "Already has a columnstore index";
            return;
        }

        double score = 0;
        var notes = new List<string>();

        // ---- 1. DATA SHAPE: repetitiveness (up to 40 pts) --------------------------------
        // % of columns that are low/medium cardinality drives compressibility.
        var dataShape =
            (t.PctLowCardinalityColumns * 0.25)          // <=1% distinct: up to 25 pts
          + (t.PctMediumCardinalityColumns * 0.15);      // <=10% distinct: up to 15 pts
        score += Math.Min(40, dataShape);

        if (t.PctLowCardinalityColumns >= 50)
            notes.Add($"{t.PctLowCardinalityColumns:0}% of columns are highly repetitive (<=1% distinct) - excellent compression expected");
        else if (t.PctMediumCardinalityColumns >= 50)
            notes.Add($"{t.PctMediumCardinalityColumns:0}% of columns are repetitive (<=10% distinct) - good compression expected");
        else if (t.SampledRows > 0)
            notes.Add("Data is mostly high-cardinality - compression benefit will be modest");

        // ---- 2. SIZE / ROWGROUP potential (up to 15 pts) ---------------------------------
        var rowgroups = t.PotentialFullRowgroups;
        switch (rowgroups)
        {
            case >= 100:
                score += 15;
                break;
            case >= 20:
                score += 12;
                break;
            case >= 5:
                score += 8;
                break;
            case >= 3:
                score += 4;
                break;
            default:
                notes.Add($"CAUTION: only ~{rowgroups:0.#} potential rowgroups ({t.RowCount:N0} rows) - likely too small to benefit");
                break;
        }

        // ---- 3. WORKLOAD: scan-heavy access (up to 20 pts) -------------------------------
        score += t.ScanPct * 0.20;
        if (t.ScanPct >= 50) notes.Add("scan-heavy access pattern");

        score += t.NcIndexBloatRatio switch
        {
            // ---- 4. NC index bloat - your reclaim thesis (up to 15 pts) ----------------------
            >= 2.0 => 15,
            >= 1.0 => 11,
            >= 0.5 => 6,
            _ => t.NcIndexBloatRatio * 12
        };
        if (t.NcIndexBloatRatio >= 1.0)
            notes.Add($"NC indexes ({t.NonclusteredMb:N0} MB) outweigh base data ({t.BaseDataMb:N0} MB) - big reclaim potential");

        // ---- 5. Contention / missing-index pressure (up to 10 pts) -----------------------
        var lockWait = t.RowLockWaitMs + t.PageLockWaitMs;
        switch (lockWait)
        {
            case >= 3_600_000:
                score += 6;
                break;
            case >= 60_000:
                score += 4;
                break;
            case > 0:
                score += 2;
                break;
        }

        switch (t.MissingIndexSuggestions)
        {
            case >= 5:
                score += 4;
                break;
            case >= 1:
                score += 2;
                break;
        }

        switch (t.WritePct)
        {
            // ---- PENALTIES --------------------------------------------------------------------
            case >= 50:
                score -= 30; notes.Add("CAUTION: write-heavy - consider nonclustered columnstore instead of clustered");
                break;
            case >= 25:
                score -= 15; notes.Add("write activity is significant - test delta-store behavior");
                break;
            case >= 10:
                score -= 5;
                break;
        }

        switch (t.UpdateShareOfWritesPct)
        {
            case >= 50:
                score -= 15; notes.Add("CAUTION: UPDATE-dominated writes fragment columnstore quickly");
                break;
            case >= 20:
                score -= 8; notes.Add("noticeable UPDATE share - plan for index maintenance");
                break;
        }

        if (t.DictionaryPressureColumns > 0)
        {
            score -= Math.Min(15, t.DictionaryPressureColumns * 5);
            notes.Add($"CAUTION: {t.DictionaryPressureColumns} high-cardinality wide string column(s) risk dictionary pressure / trimmed rowgroups");
        }

        if (t.LobColumns > 0)
        {
            score -= 5;
            notes.Add($"{t.LobColumns} LOB/unsupported column(s) present - verify columnstore compatibility on your version");
        }

        if (t.IsReplicated) notes.Add("NOTE: table is replicated");
        if (t.IsCdcTracked) notes.Add("NOTE: table is CDC-tracked");
        if (t.PageIoLatchWaitMs > 60_000)
            notes.Add("high PAGEIOLATCH waits - IO-bound scans; columnstore compression usually helps here");

        t.CandidacyScore = Math.Round(Math.Max(0, Math.Min(100, score)), 1);
        t.AssessmentNotes = string.Join("; ", notes);
    }
}
