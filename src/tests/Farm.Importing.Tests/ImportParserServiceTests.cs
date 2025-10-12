using System.IO;
using System.Text;
using System.Threading.Tasks;
using Farm.Importing.Services.Import;
using Xunit;

namespace Farm.Importing.Tests;

public class ImportParserServiceTests
{
    [Fact]
    public async Task ParseCsvAsync_BasicRow_ParsesFields()
    {
        string csv = "Name,ServerUrl,DateAcquired\nMyPrinter,http://printer.local,2020-01-02";
        var service = new ImportParserService();
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var (dtos, errors) = await service.ParseCsvAsync(ms, default);
        Assert.Empty(errors);
        Assert.Single(dtos);
        Assert.Equal("MyPrinter", dtos[0].Name);
        Assert.Equal("http://printer.local", dtos[0].ServerUrl);
    }
}
