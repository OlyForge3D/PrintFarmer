using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Discovery;

namespace Farm.Importing.Services.Import;

/// <summary>
/// Service interface for parsing printer import files.
/// </summary>
public interface IImportParserService
{
    /// <summary>
    /// Parses printer data from a CSV stream.
    /// </summary>
    /// <param name="stream">The CSV file stream.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Parsed printer DTOs and any parsing errors.</returns>
    Task<(CreatePrinterFromDiscoveryDto[] Dtos, List<string> Errors)> ParseCsvAsync(System.IO.Stream stream, CancellationToken ct);

    /// <summary>
    /// Parses printer data from a JSON stream.
    /// </summary>
    /// <param name="stream">The JSON file stream.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Parsed printer DTOs and any parsing errors.</returns>
    Task<(CreatePrinterFromDiscoveryDto[] Dtos, List<string> Errors)> ParseJsonAsync(System.IO.Stream stream, CancellationToken ct);
}
