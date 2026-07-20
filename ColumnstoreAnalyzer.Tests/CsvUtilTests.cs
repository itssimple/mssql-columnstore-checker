namespace ColumnstoreAnalyzer.Tests;

public class CsvUtilTests
{
    [Fact]
    public void Escape_PlainString_Unchanged() => Assert.Equal("hello", CsvUtil.Escape("hello"));

    [Fact]
    public void Escape_Null_ReturnsEmptyString() => Assert.Equal("", CsvUtil.Escape(null));

    [Fact]
    public void Escape_ContainingComma_IsQuoted() => Assert.Equal("\"a,b\"", CsvUtil.Escape("a,b"));

    [Fact]
    public void Escape_ContainingQuote_IsQuotedAndDoubled() =>
        Assert.Equal("\"say \"\"hi\"\"\"", CsvUtil.Escape("say \"hi\""));

    [Fact]
    public void Escape_ContainingNewlines_ReplacedWithSpace()
    {
        Assert.Equal("a b", CsvUtil.Escape("a\nb"));
        Assert.Equal("a b", CsvUtil.Escape("a\rb"));
    }

    [Fact]
    public void Escape_PlainStringWithNoSpecialChars_NotQuoted() =>
        Assert.Equal("plain text no commas", CsvUtil.Escape("plain text no commas"));
}
