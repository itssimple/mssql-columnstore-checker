namespace ColumnstoreAnalyzer.Tests;

public class BlitzAdapterTests
{
    private static DynamicResultSet MakeResultSet(List<string> columns, params object?[][] rows)
    {
        var rs = new DynamicResultSet { Name = "test", ColumnNames = columns };
        foreach (var row in rows)
        {
            var dict = new Dictionary<string, object?>();
            for (var i = 0; i < columns.Count; i++) dict[columns[i]] = row[i];
            rs.Rows.Add(dict);
        }
        return rs;
    }

    [Fact]
    public void LooksFindingShaped_TrueWhenPriorityColumnPresent()
    {
        var rs = MakeResultSet(["Priority", "Finding"], new object?[] { 1, "test" });
        Assert.True(BlitzAdapter.LooksFindingShaped(rs));
    }

    [Fact]
    public void LooksFindingShaped_FalseForRawTables()
    {
        var rs = MakeResultSet(["QueryHash", "AvgCPU"], new object?[] { 123L, 45.6 });
        Assert.False(BlitzAdapter.LooksFindingShaped(rs));
    }

    [Theory]
    [InlineData(1, HealthCheckSeverity.Critical)]
    [InlineData(50, HealthCheckSeverity.Critical)]
    [InlineData(51, HealthCheckSeverity.High)]
    [InlineData(100, HealthCheckSeverity.High)]
    [InlineData(101, HealthCheckSeverity.Medium)]
    [InlineData(150, HealthCheckSeverity.Medium)]
    [InlineData(151, HealthCheckSeverity.Low)]
    [InlineData(200, HealthCheckSeverity.Low)]
    [InlineData(201, HealthCheckSeverity.Info)]
    public void ToFindings_MapsPriorityToSeverity(int priority, HealthCheckSeverity expected)
    {
        var rs = MakeResultSet(
            ["Priority", "FindingsGroup", "Finding", "URL", "Details", "DatabaseName"],
            new object?[] { priority, "Perf", "Something found", "http://example.com", "detail text", "MyDb" });

        var findings = BlitzAdapter.ToFindings("sp_Blitz", rs);

        Assert.Single(findings);
        Assert.Equal(expected, findings[0].Severity);
        Assert.Equal("MyDb", findings[0].DatabaseName);
        Assert.Equal("Something found", findings[0].Title);
        Assert.Equal("Perf", findings[0].Category);
        Assert.Equal("detail text", findings[0].Details);
        Assert.Equal("http://example.com", findings[0].Recommendation);
    }

    [Fact]
    public void ToFindings_HandlesCaseInsensitiveColumnNames()
    {
        var rs = MakeResultSet(["priority", "finding"], new object?[] { 10, "lowercase columns" });
        var findings = BlitzAdapter.ToFindings("sp_Blitz", rs);
        Assert.Single(findings);
        Assert.Equal("lowercase columns", findings[0].Title);
    }

    [Fact]
    public void ToFindings_UnrecognizedSchema_FallsBackToRawRowInfo()
    {
        var rs = MakeResultSet(["SomeWeirdColumn", "AnotherOne"], new object?[] { "value1", "value2" });
        var findings = BlitzAdapter.ToFindings("sp_Blitz", rs);

        Assert.Single(findings);
        Assert.Equal(HealthCheckSeverity.Info, findings[0].Severity);
        Assert.Contains("SomeWeirdColumn=value1", findings[0].Details);
    }

    [Fact]
    public void ToFindings_NonNumericPriority_FallsBackToInfo()
    {
        var rs = MakeResultSet(["Priority", "Finding"], new object?[] { "not-a-number", "weird finding" });
        var findings = BlitzAdapter.ToFindings("sp_Blitz", rs);
        Assert.Equal(HealthCheckSeverity.Info, findings[0].Severity);
    }

    [Fact]
    public void ToFindings_MissingTitle_FallsBackToGenericTitle()
    {
        var rs = MakeResultSet(["Priority"], new object?[] { 10 });
        var findings = BlitzAdapter.ToFindings("sp_BlitzIndex", rs);
        Assert.Equal("sp_BlitzIndex finding", findings[0].Title);
    }

    [Fact]
    public void ToFindings_MultipleRows_ProducesOneFindingPerRow()
    {
        var rs = MakeResultSet(
            ["Priority", "Finding"],
            new object?[] { 10, "First" },
            new object?[] { 20, "Second" });

        var findings = BlitzAdapter.ToFindings("sp_Blitz", rs);

        Assert.Equal(2, findings.Count);
        Assert.Equal("First", findings[0].Title);
        Assert.Equal("Second", findings[1].Title);
    }
}
