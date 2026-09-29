namespace Farm.Moonraker.Emulator.Domain;

/// <summary>One observed Moonraker protocol request (REST call or JSON-RPC method).</summary>
public sealed record RequestLogEntry(
    long Sequence,
    DateTimeOffset ReceivedAtUtc,
    string Transport,
    string Method,
    string Target,
    bool IsCommand);

/// <summary>Point-in-time copy of the <see cref="RequestLog"/>.</summary>
public sealed record RequestLogSnapshot(
    long Total,
    long Commands,
    IReadOnlyList<RequestLogEntry> Entries);

/// <summary>
/// Records every emulated Moonraker protocol request this process receives so a test
/// (for example the offline-update recovery matrix) can prove whether a PrintFarmer
/// host issued any printer command during a window. A request is classified as a
/// <em>command</em> conservatively: every REST call using a method other than
/// GET/HEAD/OPTIONS (except the JSON-RPC-over-HTTP fallback, which is classified per
/// RPC method instead, and a <c>POST /server/spoolman/proxy</c> whose forwarded
/// <c>request_method</c> is itself GET/HEAD/OPTIONS) and every JSON-RPC method outside a
/// fixed read-only set. The
/// <c>/__emulator/**</c> control surface and <c>/healthz</c> are never recorded.
/// Counters are cumulative for the process lifetime and are deliberately NOT cleared
/// by <c>/__emulator/reset</c>, so a reset can never hide an earlier command. Only the
/// newest <see cref="Capacity"/> entries are retained.
/// </summary>
public sealed class RequestLog
{
    public const int Capacity = 2000;

    private static readonly HashSet<string> SafeHttpMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET",
        "HEAD",
        "OPTIONS",
    };

    private static readonly HashSet<string> ReadOnlyRpcMethods = new(StringComparer.Ordinal)
    {
        "server.connection.identify",
        "server.info",
        "printer.objects.list",
        "printer.objects.subscribe",
        "printer.objects.query",
        "camera.start_monitor",
        "camera.stop_monitor",
        "server.files.get_directory",
    };

    private readonly object _gate = new();
    private readonly List<RequestLogEntry> _entries = [];
    private readonly TimeProvider _timeProvider;
    private long _sequence;
    private long _commands;

    public RequestLog()
        : this(TimeProvider.System)
    {
    }

    public RequestLog(TimeProvider timeProvider) => _timeProvider = timeProvider;

    public const string SpoolmanProxyPath = "/server/spoolman/proxy";

    /// <summary>
    /// Classifies a REST call. <paramref name="proxiedMethod"/> is the <c>request_method</c>
    /// forwarded by a <c>POST /server/spoolman/proxy</c> call: that proxy is a read only when
    /// the forwarded method is itself safe. A missing or unreadable forwarded method stays a command.
    /// </summary>
    public static bool IsHttpCommand(string method, string path, string? proxiedMethod = null)
    {
        if (SafeHttpMethods.Contains(method) || path.Equals("/websocket", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !(path.TrimEnd('/').Equals(SpoolmanProxyPath, StringComparison.OrdinalIgnoreCase) &&
                 proxiedMethod is not null &&
                 SafeHttpMethods.Contains(proxiedMethod));
    }

    public static bool IsRpcCommand(string method) => !ReadOnlyRpcMethods.Contains(method);

    public RequestLogEntry RecordHttp(string method, string path, string? proxiedMethod = null) =>
        Record("http", method.ToUpperInvariant(), path, IsHttpCommand(method, path, proxiedMethod));

    public RequestLogEntry RecordRpc(string method) =>
        Record("jsonrpc", method, method, IsRpcCommand(method));

    public void Retarget(long sequence, string target)
    {
        lock (_gate)
        {
            int index = _entries.FindIndex(entry => entry.Sequence == sequence);
            if (index >= 0)
            {
                RequestLogEntry entry = _entries[index];
                _entries[index] = entry with { Target = target };
            }
        }
    }

    public RequestLogSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new RequestLogSnapshot(_sequence, _commands, [.. _entries]);
        }
    }

    private RequestLogEntry Record(string transport, string method, string target, bool isCommand)
    {
        lock (_gate)
        {
            _sequence++;
            if (isCommand)
            {
                _commands++;
            }

            RequestLogEntry entry = new(_sequence, _timeProvider.GetUtcNow(), transport, method, target, isCommand);
            _entries.Add(entry);
            while (_entries.Count > Capacity)
            {
                _entries.RemoveAt(0);
            }

            return entry;
        }
    }
}
