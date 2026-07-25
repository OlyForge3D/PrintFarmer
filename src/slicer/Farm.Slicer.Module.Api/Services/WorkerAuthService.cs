using System.Security.Cryptography;
using System.Text;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Microsoft.Extensions.Primitives;

namespace Farm.Slicer.Module.Api.Services;

public sealed class WorkerAuthService(IWorkerRepository workerRepository) : IWorkerAuthService
{
    private readonly IWorkerRepository _workerRepository = workerRepository;

    public const string HeaderName = "X-Worker-Key";
    public const string ServiceIdHeaderName = "X-Worker-Id";

    public async Task<Worker?> AuthenticateAsync(HttpContext httpContext)
    {
        if (httpContext is null ||
            !httpContext.Request.Headers.TryGetValue(ServiceIdHeaderName, out StringValues serviceValues) ||
            !Guid.TryParse(serviceValues.FirstOrDefault(), out Guid serviceId) ||
            !httpContext.Request.Headers.TryGetValue(HeaderName, out StringValues keyValues))
        {
            return null;
        }

        string? presentedKey = keyValues.FirstOrDefault();
        Worker? worker = await _workerRepository.GetByServiceIdAsync(serviceId.ToString());
        if (worker is null ||
            worker.IsDisabled ||
            string.IsNullOrEmpty(worker.ApiKey) ||
            string.IsNullOrEmpty(presentedKey))
        {
            return null;
        }

        byte[] presentedBytes = Encoding.UTF8.GetBytes(presentedKey);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(worker.ApiKey);
        return presentedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(presentedBytes, expectedBytes)
            ? worker
            : null;
    }
}
