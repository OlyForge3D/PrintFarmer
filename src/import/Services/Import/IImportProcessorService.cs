using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Web.Shared;

namespace Farm.Importing.Services.Import;

public interface IImportProcessorService
{
    Task<List<(string Name, string Status, System.Guid? Id, string? Reason)>> ProcessAsync(CreatePrinterDto[] dtos, string duplicateHandling, CancellationToken ct);
}
