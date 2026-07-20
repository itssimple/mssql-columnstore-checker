namespace ColumnstoreAnalyzer.Tests;

public class FindingsUtilTests
{
    private static HealthCheckFinding MakeFinding(
        string source = "Native", string category = "Security", HealthCheckSeverity severity = HealthCheckSeverity.Info,
        string title = "Title", string details = "Details", string? database = null, string? obj = null) =>
        new() { Source = source, Category = category, Severity = severity, Title = title, Details = details, DatabaseName = database, ObjectName = obj };

    [Fact]
    public void FilterToDatabase_DropsOtherDatabaseFindings()
    {
        var findings = new List<HealthCheckFinding>
        {
            MakeFinding(database: "DbA"),
            MakeFinding(database: "DbB"),
            MakeFinding(database: null), // server-wide, no scope - must never be dropped
        };

        FindingsUtil.FilterToDatabase(findings, "DbA", allDatabases: false);

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.DatabaseName == "DbA");
        Assert.Contains(findings, f => f.DatabaseName == null);
        Assert.DoesNotContain(findings, f => f.DatabaseName == "DbB");
    }

    [Fact]
    public void FilterToDatabase_CaseInsensitiveMatch()
    {
        var findings = new List<HealthCheckFinding> { MakeFinding(database: "MyDb") };
        FindingsUtil.FilterToDatabase(findings, "MYDB", allDatabases: false);
        Assert.Single(findings);
    }

    [Fact]
    public void FilterToDatabase_AllDatabases_KeepsEverything()
    {
        var findings = new List<HealthCheckFinding> { MakeFinding(database: "DbA"), MakeFinding(database: "DbB") };
        FindingsUtil.FilterToDatabase(findings, "DbA", allDatabases: true);
        Assert.Equal(2, findings.Count);
    }

    [Fact]
    public void Deduplicate_RemovesExactDuplicates_KeepsFirst()
    {
        var findings = new List<HealthCheckFinding>
        {
            MakeFinding(title: "Same"),
            MakeFinding(title: "Same"),
            MakeFinding(title: "Different"),
        };

        FindingsUtil.Deduplicate(findings);

        Assert.Equal(2, findings.Count);
    }

    [Fact]
    public void Deduplicate_DifferentDatabaseName_NotConsideredDuplicate()
    {
        var findings = new List<HealthCheckFinding>
        {
            MakeFinding(title: "Same", database: "DbA"),
            MakeFinding(title: "Same", database: "DbB"),
        };

        FindingsUtil.Deduplicate(findings);

        Assert.Equal(2, findings.Count);
    }

    [Fact]
    public void Deduplicate_DifferentDetails_NotConsideredDuplicate()
    {
        var findings = new List<HealthCheckFinding>
        {
            MakeFinding(title: "Same", details: "Detail A"),
            MakeFinding(title: "Same", details: "Detail B"),
        };

        FindingsUtil.Deduplicate(findings);

        Assert.Equal(2, findings.Count);
    }
}
