using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Shared;

namespace Farm.Importing.Services.Import;

public interface IImportParserService
{
    Task<(CreatePrinterDto[] Dtos, List<string> Errors)> ParseCsvAsync(System.IO.Stream stream, CancellationToken ct);
    Task<(CreatePrinterDto[] Dtos, List<string> Errors)> ParseJsonAsync(System.IO.Stream stream, CancellationToken ct);
}
