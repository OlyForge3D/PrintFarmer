using System.Net;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Guarantees certified by an adapter, never inferred from inventory version metadata.</summary>
public enum PrinterFileDeleteGuarantee
{
    Unsupported,
    CurrentExactPath,
    NativeConditional,
}

public enum PrinterFileDeleteOutcome
{
    Acknowledged,
    Rejected,
    ObservedAbsent,
    OutcomeUnknown,
}

/// <summary>No provider body, URL or credentials may escape through this result.</summary>
public sealed record PrinterFileDeleteResult(PrinterFileDeleteOutcome Outcome);

/// <summary>
/// Separate from legacy boolean delete. Implementations send at most once, propagate the
/// caller token, refuse folders/redirects and preserve uncertainty after possible send.
/// NativeConditional implementations MUST atomically compare the supplied provider version.
/// </summary>
public interface ISupportsSafePrinterFileDelete
{
    PrinterFileDeleteGuarantee GetDeleteGuarantee(string storageRoot, string relativePath);

    Task<PrinterFileDeleteResult> DeleteExactFileAsync(
        string baseUrl, string storageRoot, string relativePath, string? providerVersion,
        PrinterCredential? credential, CancellationToken ct);
}

/// <summary>
/// Native single-send HTTP protocol. The injected transport must disable redirects and
/// mutation retries (as required for certified copy transports too). No legacy retry wrapper.
/// </summary>
public static class PrinterFileDeleteTransport
{
    public static bool IsExactFile(string root, string path, string requiredRoot) =>
        root == requiredRoot &&
        PrinterInventoryPath.TryCanonicalizeRelativePath(path, out string canonical) &&
        path == canonical && !path.Contains('%');

    public static async Task<PrinterFileDeleteResult> DeleteAsync(
        HttpClient http, string baseUrl, string root, string path, string requiredRoot,
        string prefix, PrinterCredential? credential, CancellationToken ct)
    {
        // Validation failure is known before sending; the service normally catches this at intent.
        Uri uri = PrinterFileCopyTransport.BuildUri(baseUrl, root, path, requiredRoot, prefix);
        ct.ThrowIfCancellationRequested();
        using HttpRequestMessage request = new(HttpMethod.Delete, uri);
        if (credential?.HasApiKey == true)
        {
            request.Headers.Add("X-Api-Key", credential.ApiKey);
        }

        try
        {
            using HttpResponseMessage response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            // Only certified synchronous success statuses count as an ACK. 202 is NOT completion.
            return new(response.StatusCode switch
            {
                HttpStatusCode.OK or HttpStatusCode.NoContent => PrinterFileDeleteOutcome.Acknowledged,
                HttpStatusCode.NotFound => PrinterFileDeleteOutcome.ObservedAbsent,
                HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or
                    HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or
                    HttpStatusCode.MethodNotAllowed => PrinterFileDeleteOutcome.Rejected,
                _ => PrinterFileDeleteOutcome.OutcomeUnknown,
            });
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException or OperationCanceledException)
        {
            return new(PrinterFileDeleteOutcome.OutcomeUnknown);
        }
    }
}
