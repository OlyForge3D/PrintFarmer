using Farm.Web.Api.Services.Printers;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class CsvImportParserTests
{
    [Fact]
    public void SplitCsvLine_ReturnsColumns_ForQuotedLine()
    {
        const string csvLine = "\"Moonraker Printer\",\"192.168.1.100\",\"Moonraker\",\"7125\",\"80\",\"Creality\",\"Ender-3 Max\",\"Main production printer\",\"false\"";

        string[] columns = CsvImportParser.SplitCsvLine(csvLine);

        Assert.Equal(9, columns.Length);
        Assert.Equal("Moonraker Printer", columns[0]);
        Assert.Equal("192.168.1.100", columns[1]);
        Assert.Equal("Moonraker", columns[2]);
        Assert.Equal("7125", columns[3]);
        Assert.Equal("80", columns[4]);
        Assert.Equal("Creality", columns[5]);
        Assert.Equal("Ender-3 Max", columns[6]);
        Assert.Equal("Main production printer", columns[7]);
        Assert.Equal("false", columns[8]);
    }

    [Fact]
    public void SplitCsvLine_UnescapesDoubleQuotes()
    {
        const string csvLine = "\"Quote \"\"inside\"\"\",\"value\"";

        string[] columns = CsvImportParser.SplitCsvLine(csvLine);

        Assert.Equal(2, columns.Length);
        Assert.Equal("Quote \"inside\"", columns[0]);
        Assert.Equal("value", columns[1]);
    }
}
