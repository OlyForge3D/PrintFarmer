using System.Net;
using System.Text.Json;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Shared bounded metadata transport and authority bookkeeping for native backend adapters.</summary>
public sealed class PrinterFileInventorySession : IDisposable
{
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private readonly CancellationToken _callerToken;
    private readonly CancellationTokenSource _deadline;
    private readonly List<PrinterFileInventoryEntry> _entries = [];
    private readonly List<PrinterFileInventoryCoverage> _coverage = [];
    private readonly HashSet<string> _identities = new(StringComparer.Ordinal);
    private int _requests;
    private int _visitedEntries;

    public PrinterFileInventorySession(PrinterFileInventoryOptions? options, CancellationToken callerToken)
    {
        Options = options ?? new();
        Options.Validate();
        _callerToken = callerToken;
        _deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        _deadline.CancelAfter(Options.Deadline);
    }

    public PrinterFileInventoryOptions Options { get; }

    public CancellationToken Token => _deadline.Token;

    /// <summary>Counts folders and files, including non-printable entries, to bound traversal.</summary>
    public void VisitEntry(int depth)
    {
        Token.ThrowIfCancellationRequested();
        if (++_visitedEntries > Options.MaxEntries)
        {
            throw new PrinterFileInventoryException(PrinterFileInventoryReason.EntryLimit);
        }

        if (depth > Options.MaxDepth)
        {
            throw new PrinterFileInventoryException(PrinterFileInventoryReason.DepthLimit);
        }
    }

    /// <summary>Adds a validated observation and rejects duplicate or ambiguous provider identities.</summary>
    public void Add(PrinterFileInventoryEntry entry)
    {
        if (!PrinterInventoryPath.TryCanonicalizeRoot(entry.StorageRoot, out string root) ||
            root != entry.StorageRoot ||
            !PrinterInventoryPath.TryCanonicalizeRelativePath(entry.RelativePath, out _) ||
            !_identities.Add(PrinterInventoryPath.ComputeIdentity(root, entry.RelativePath)))
        {
            throw new PrinterFileInventoryException(PrinterFileInventoryReason.InvalidPath);
        }

        _entries.Add(entry);
    }

    /// <summary>Records only evidence established by the native adapter, never inferred from file count.</summary>
    public void Cover(string root, bool complete, PrinterFileInventoryEvidence evidence) =>
        _coverage.Add(new(root, string.Empty, complete, evidence));

    /// <summary>Reads one bounded JSON listing; the sender must use the supplied cancellation token.</summary>
    public async Task<JsonDocument> ReadAsync(Func<CancellationToken, Task<HttpResponseMessage>> send, int requestCost = 1)
    {
        Token.ThrowIfCancellationRequested();
        if (requestCost < 1 || requestCost > Options.MaxRequests - _requests)
        {
            throw new PrinterFileInventoryException(PrinterFileInventoryReason.RequestLimit);
        }

        _requests += requestCost;
        using HttpResponseMessage response = await send(Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new PrinterFileInventoryException(PrinterFileInventoryReason.AuthenticationRequired);
        }

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
        {
            throw new PrinterFileInventoryException(PrinterFileInventoryReason.Unsupported);
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new PrinterFileInventoryException(PrinterFileInventoryReason.TransportFailure);
        }

        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new PrinterFileInventoryException(PrinterFileInventoryReason.ResponseLimit);
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(Token).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, Token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaxResponseBytes)
            {
                throw new PrinterFileInventoryException(PrinterFileInventoryReason.ResponseLimit);
            }

            await buffer.WriteAsync(chunk.AsMemory(0, count), Token).ConfigureAwait(false);
        }

        if (response.Content.Headers.ContentLength is long expectedLength && expectedLength != buffer.Length)
        {
            throw new PrinterFileInventoryException(PrinterFileInventoryReason.IncompleteResponse);
        }

        Token.ThrowIfCancellationRequested();
        return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 128 });
    }

    /// <summary>Builds a complete result only after every declared readable root has completed.</summary>
    public PrinterFileInventoryResult Complete()
    {
        Token.ThrowIfCancellationRequested();
        return new(PrinterFileInventoryOutcome.Complete, PrinterFileInventoryReason.None,
            [.. _entries], [.. _coverage]);
    }

    /// <summary>Classifies failure without exposing transport/body details or manufacturing empty success.</summary>
    public PrinterFileInventoryResult Failed(Exception exception)
    {
        PrinterFileInventoryReason reason = exception switch
        {
            OperationCanceledException when _callerToken.IsCancellationRequested => PrinterFileInventoryReason.Cancelled,
            OperationCanceledException => PrinterFileInventoryReason.Timeout,
            PrinterFileInventoryException failure => failure.Reason,
            JsonException or InvalidOperationException or FormatException or KeyNotFoundException =>
                PrinterFileInventoryReason.MalformedResponse,
            _ => PrinterFileInventoryReason.TransportFailure
        };
        PrinterFileInventoryOutcome outcome = reason switch
        {
            PrinterFileInventoryReason.Cancelled => PrinterFileInventoryOutcome.Cancelled,
            PrinterFileInventoryReason.Unsupported when _entries.Count == 0 && _coverage.Count == 0 =>
                PrinterFileInventoryOutcome.Unsupported,
            _ when _entries.Count > 0 || _coverage.Count > 0 ||
                reason is PrinterFileInventoryReason.EntryLimit or PrinterFileInventoryReason.DepthLimit or
                    PrinterFileInventoryReason.RequestLimit or PrinterFileInventoryReason.IncompleteResponse =>
                PrinterFileInventoryOutcome.Partial,
            _ => PrinterFileInventoryOutcome.Unavailable
        };
        return new(outcome, reason, [.. _entries], [.. _coverage]);
    }

    /// <summary>Extracts optional nonnegative byte size without replacing unknown values with zero.</summary>
    public static long? Size(JsonElement item, string property = "size") =>
        item.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out long size) && size >= 0
            ? size : null;

    /// <summary>Extracts optional Unix time while preserving missing or invalid timestamps as unknown.</summary>
    public static DateTimeOffset? Modified(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double seconds) || !double.IsFinite(seconds) ||
            seconds < -62135596800 || seconds > 253402300799)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
    }

    /// <summary>Retains the provider's bounded raw numeric modification value without precision loss.</summary>
    public static string? ModifiedValue(JsonElement item, string property) =>
        Modified(item, property) is not null && item.TryGetProperty(property, out JsonElement value) &&
            value.GetRawText() is { Length: <= 128 } raw ? raw : null;

    /// <summary>Rejects explicit pagination/truncation signals rather than claiming root completion.</summary>
    public static void RequireUntruncated(JsonElement item)
    {
        if (item.TryGetProperty("error", out JsonElement error) && error.ValueKind != JsonValueKind.Null)
        {
            throw new PrinterFileInventoryException(PrinterFileInventoryReason.MalformedResponse);
        }

        foreach (string property in new[] { "truncated", "has_more", "hasMore" })
        {
            if (item.TryGetProperty(property, out JsonElement value) && value.ValueKind != JsonValueKind.False)
            {
                throw new PrinterFileInventoryException(PrinterFileInventoryReason.IncompleteResponse);
            }
        }

        if (item.TryGetProperty("next", out JsonElement next) && next.ValueKind != JsonValueKind.Null)
        {
            throw new PrinterFileInventoryException(PrinterFileInventoryReason.IncompleteResponse);
        }
    }

    public void Dispose() => _deadline.Dispose();
}

/// <summary>Internal protocol failures represented by a stable safe inventory reason.</summary>
public sealed class PrinterFileInventoryException : Exception
{
    public PrinterFileInventoryException()
    {
    }

    public PrinterFileInventoryException(string message) : base(message)
    {
    }

    public PrinterFileInventoryException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public PrinterFileInventoryException(PrinterFileInventoryReason reason)
    {
        Reason = reason;
    }

    public PrinterFileInventoryReason Reason { get; } = PrinterFileInventoryReason.MalformedResponse;
}
