using System.Net;
using System.Security.Cryptography;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Independently certified native, authenticated file transfer. Inventory and the legacy
/// byte-array download capability do not imply this capability. The caller owns the destination.
/// Implementations must use a redirect-disabled transport.
/// </summary>
public interface ISupportsPrinterFileCopy
{
    Task<PrinterFileCopyBytes> CopyFileToAsync(
        string baseUrl, string storageRoot, string relativePath, PrinterCredential? credential,
        Stream destination, long maximumBytes, CancellationToken ct);
}

/// <summary>Integrity of received bytes, deliberately not a remote snapshot guarantee.</summary>
public sealed record PrinterFileCopyBytes(string Sha256, long SizeBytes);

/// <summary>Non-sensitive transport failure; upstream URLs and bodies must not reach operation results.</summary>
public sealed class PrinterFileCopyException : IOException
{
    public PrinterFileCopyException(string reason) : base(reason) => Reason = reason;

    public PrinterFileCopyException() : this("transfer_failed")
    {
    }

    public PrinterFileCopyException(string message, Exception innerException) : base(message, innerException) =>
        Reason = "transfer_failed";

    public string Reason { get; }
}

/// <summary>Shared bounded streaming protocol for certified native download endpoints.</summary>
public static class PrinterFileCopyTransport
{
    public static Uri BuildUri(string baseUrl, string root, string path, string requiredRoot, string prefix)
    {
        // Refuse encoded separators/dot segments even on servers that decode paths twice.
        if (root != requiredRoot ||
            !PrinterInventoryPath.TryCanonicalizeRelativePath(path, out _) ||
            path.Contains('%') ||
            !Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out Uri? source) ||
            source.Scheme is not ("http" or "https") || source.UserInfo.Length != 0 ||
            source.Query.Length != 0 || source.Fragment.Length != 0)
        {
            throw new PrinterFileCopyException("invalid_source_path");
        }

        return new Uri(source, prefix + string.Join('/', path.Split('/').Select(Uri.EscapeDataString)));
    }

    public static async Task<PrinterFileCopyBytes> CopyAsync(
        HttpClient http, Uri uri, PrinterCredential? credential,
        Stream destination, long maximumBytes, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        if (credential?.HasApiKey == true)
        {
            request.Headers.Add("X-Api-Key", credential.ApiKey);
        }

        using HttpResponseMessage response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        return await CopyResponseAsync(response, destination, maximumBytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the certified redirect, status, encoding, and length checks to a response obtained
    /// from an adapter-owned redirect-disabled transport (for example, one performing Digest auth).
    /// </summary>
    public static async Task<PrinterFileCopyBytes> CopyResponseAsync(
        HttpResponseMessage response, Stream destination, long maximumBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new PrinterFileCopyException("redirect_refused");
        }

        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentEncoding.Count != 0)
        {
            throw new PrinterFileCopyException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "authentication_required",
                HttpStatusCode.NotFound => "source_missing",
                _ => "transfer_failed",
            });
        }

        long? expected = response.Content.Headers.ContentLength;
        if (expected > maximumBytes)
        {
            throw new PrinterFileCopyException("size_limit");
        }

        await using Stream content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await CopyBoundedAsync(content, destination, maximumBytes, expected, ct).ConfigureAwait(false);
    }

    /// <summary>Also used for local ingest: actual bytes, never metadata, establish content identity.</summary>
    public static async Task<PrinterFileCopyBytes> CopyBoundedAsync(
        Stream source, Stream destination, long maximumBytes, long? expectedBytes, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (count > maximumBytes - total)
            {
                throw new PrinterFileCopyException("size_limit");
            }

            total += count;
            digest.AppendData(buffer, 0, count);
            await destination.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        if (total == 0 || (expectedBytes.HasValue && expectedBytes.Value != total))
        {
            throw new PrinterFileCopyException("incomplete_response");
        }

        await destination.FlushAsync(ct).ConfigureAwait(false);
        return new(Convert.ToHexString(digest.GetHashAndReset()), total);
    }
}
