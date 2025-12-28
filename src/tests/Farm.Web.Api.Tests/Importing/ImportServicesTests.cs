using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Importing.Services.Adapters;
using Farm.Importing.Services.Import;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Web.Api.Tests.Importing;

public class ImportParserServiceTests
{
    [Fact]
    public async Task ParseCsvAsync_WithQuotedFields_PreservesCommas()
    {
        string csv = "Name,ServerUrl,Notes\n\"My, Printer\",http://printer.local,\"note,1\"";
        var service = new ImportParserService();

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var (dtos, errors) = await service.ParseCsvAsync(ms, CancellationToken.None);

        errors.Should().BeEmpty();
        dtos.Should().ContainSingle();
        dtos[0].Name.Should().Be("My, Printer");
        dtos[0].Notes.Should().Be("note,1");
    }

    [Fact]
    public async Task ParseJsonAsync_WithInvalidPayload_ReturnsError()
    {
        var service = new ImportParserService();
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("not-json"));

        var (dtos, errors) = await service.ParseJsonAsync(ms, CancellationToken.None);

        dtos.Should().BeEmpty();
        errors.Should().ContainSingle();
    }
}


