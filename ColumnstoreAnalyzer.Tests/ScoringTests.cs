namespace ColumnstoreAnalyzer.Tests;

public class ScoringTests
{
    private static TableInfo MakeTable(
        long rowCount = 5_000_000, decimal baseDataMb = 5000, decimal ncMb = 0,
        bool hasColumnstore = false,
        long seeks = 0, long scans = 0, long lookups = 0, long updates = 0,
        double pctLow = 0, double pctMed = 0) =>
        new()
        {
            ObjectId = 1, SchemaName = "dbo", TableName = "T", RowCount = rowCount,
            TotalSizeMb = baseDataMb + ncMb, BaseDataMb = baseDataMb, NonclusteredMb = ncMb,
            HasColumnstore = hasColumnstore,
            UserSeeks = seeks, UserScans = scans, UserLookups = lookups, UserUpdates = updates,
            PctLowCardinalityColumns = pctLow, PctMediumCardinalityColumns = pctMed
        };

    [Fact]
    public void Score_AlreadyColumnstore_ScoresZero()
    {
        var t = MakeTable(hasColumnstore: true);
        Scoring.Score(t);
        Assert.Equal(0, t.CandidacyScore);
        Assert.Contains("Already has a columnstore index", t.AssessmentNotes);
    }

    [Fact]
    public void Score_TinyTable_FlaggedTooSmall()
    {
        var t = MakeTable(rowCount: 1000, baseDataMb: 1);
        Scoring.Score(t);
        Assert.Contains("too small to benefit", t.AssessmentNotes);
    }

    [Fact]
    public void Score_HighlyRepetitiveScanHeavyBigTable_ScoresAsStrongCandidate()
    {
        var t = MakeTable(
            rowCount: 50_000_000, baseDataMb: 5000, ncMb: 8000,
            seeks: 10, scans: 900, lookups: 0, updates: 5,
            pctLow: 80, pctMed: 95);

        Scoring.Score(t);

        Assert.True(t.CandidacyScore >= 55, $"expected a strong-candidate score (>=55), got {t.CandidacyScore}");
    }

    [Fact]
    public void Score_WriteHeavyTable_ScoresLowerThanEquivalentReadHeavyTable()
    {
        var writeHeavy = MakeTable(
            rowCount: 50_000_000, baseDataMb: 5000, ncMb: 8000,
            seeks: 10, scans: 10, lookups: 0, updates: 500,
            pctLow: 80, pctMed: 95);
        var readHeavy = MakeTable(
            rowCount: 50_000_000, baseDataMb: 5000, ncMb: 8000,
            seeks: 10, scans: 900, lookups: 0, updates: 5,
            pctLow: 80, pctMed: 95);

        Scoring.Score(writeHeavy);
        Scoring.Score(readHeavy);

        Assert.True(writeHeavy.CandidacyScore < readHeavy.CandidacyScore);
        Assert.Contains("write-heavy", writeHeavy.AssessmentNotes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Score_DictionaryPressureColumns_LowersScore()
    {
        var clean = MakeTable(rowCount: 50_000_000, baseDataMb: 5000, ncMb: 8000, scans: 900, pctLow: 80, pctMed: 95);
        var withPressure = MakeTable(rowCount: 50_000_000, baseDataMb: 5000, ncMb: 8000, scans: 900, pctLow: 80, pctMed: 95);
        withPressure.DictionaryPressureColumns = 3;

        Scoring.Score(clean);
        Scoring.Score(withPressure);

        Assert.True(withPressure.CandidacyScore < clean.CandidacyScore);
    }

    [Fact]
    public void Score_IsAlwaysClampedBetweenZeroAndHundred()
    {
        var worstCase = MakeTable(rowCount: 1, baseDataMb: 0.1m, updates: 1_000_000, seeks: 0, scans: 0);
        worstCase.DictionaryPressureColumns = 50;
        worstCase.LobColumns = 10;

        Scoring.Score(worstCase);

        Assert.InRange(worstCase.CandidacyScore, 0, 100);
    }

    [Fact]
    public void Score_NcIndexBloat_IncreasesScore()
    {
        var noBloat = MakeTable(rowCount: 50_000_000, baseDataMb: 5000, ncMb: 0, scans: 900, pctLow: 80, pctMed: 95);
        var heavyBloat = MakeTable(rowCount: 50_000_000, baseDataMb: 5000, ncMb: 12000, scans: 900, pctLow: 80, pctMed: 95);

        Scoring.Score(noBloat);
        Scoring.Score(heavyBloat);

        Assert.True(heavyBloat.CandidacyScore > noBloat.CandidacyScore);
    }
}
