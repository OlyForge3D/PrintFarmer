namespace Farm.Moonraker.Emulator.Domain;

/// <summary>Which transport a fault rule matches against.</summary>
public enum FaultTarget
{
    Http,
    WebSocket,
}

/// <summary>What the matched request/connection should experience.</summary>
public enum FaultEffect
{
    /// <summary>Delay the response/processing by <see cref="FaultRule.LatencyMs"/>.</summary>
    Latency,

    /// <summary>Short-circuit an HTTP request with <see cref="FaultRule.HttpStatusCode"/> and <see cref="FaultRule.HttpBody"/>.</summary>
    HttpStatus,

    /// <summary>Reply to a JSON-RPC request with a <see cref="FaultRule.RpcErrorCode"/>/<see cref="FaultRule.RpcErrorMessage"/> error instead of a result.</summary>
    RpcError,

    /// <summary>Send back deliberately invalid JSON instead of a well-formed response/notification.</summary>
    MalformedJson,

    /// <summary>Forcibly close the matching WebSocket connection.</summary>
    WsDisconnect,

    /// <summary>Silence outgoing notify_status_update broadcasts for <see cref="FaultRule.StaleSeconds"/>.</summary>
    StaleNotifications,

    /// <summary>Force the request to behave as though Klippy were disconnected (HTTP 503 / "Klippy is not connected").</summary>
    KlippyUnavailable,
}

/// <summary>One test-control fault-injection rule (REST or WebSocket), scoped to a printer or global.</summary>
public sealed class FaultRule
{
    /// <summary>
    /// Intentionally non-deterministic: rules are ephemeral test-control constructs, not
    /// part of the emulated Moonraker wire protocol, and are always returned synchronously
    /// in the same HTTP response to whichever caller created them (see
    /// <c>ControlApiEndpoints</c>'s POST <c>/__emulator/rules</c>), so nothing needs to
    /// predict this id ahead of time or across a printer reset — callers capture it from
    /// the create response before referencing it again (e.g. to delete it).
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Printer id this rule applies to, or null to match every printer.</summary>
    public string? PrinterId { get; init; }

    public required FaultTarget Target { get; init; }

    /// <summary>Case-insensitive substring match against the request path (Http target only). Null matches any path.</summary>
    public string? PathContains { get; init; }

    /// <summary>Exact HTTP method match (Http target only). Null matches any method.</summary>
    public string? Method { get; init; }

    /// <summary>Exact JSON-RPC method match (WebSocket target only). Null matches any method.</summary>
    public string? RpcMethod { get; init; }

    public required FaultEffect Effect { get; init; }

    public int? LatencyMs { get; init; }

    public int? HttpStatusCode { get; init; }

    public string? HttpBody { get; init; }

    public int? RpcErrorCode { get; init; }

    public string? RpcErrorMessage { get; init; }

    public double? StaleSeconds { get; init; }

    /// <summary>When true the rule stays active across every match; otherwise it is consumed after <see cref="RemainingUses"/> matches.</summary>
    public bool Repeating { get; init; }

    public int RemainingUses { get; set; } = 1;
}

/// <summary>
/// Holds and matches fault-injection rules created through the <c>/__emulator/**</c>
/// control API. Only reachable when <c>Emulator:EnableControlApi=true</c>.
/// </summary>
public sealed class FaultRuleEngine
{
    private readonly ConcurrentDictionary<string, FaultRule> _rules = new(StringComparer.Ordinal);

    public FaultRule Add(FaultRule rule)
    {
        _rules[rule.Id] = rule;
        return rule;
    }

    public bool Remove(string id) => _rules.TryRemove(id, out _);

    public void Clear() => _rules.Clear();

    public IReadOnlyList<FaultRule> List() => _rules.Values.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();

    public FaultRule? MatchHttp(string printerId, string method, string path)
    {
        foreach (FaultRule rule in _rules.Values)
        {
            if (rule.Target != FaultTarget.Http)
            {
                continue;
            }

            if (rule.PrinterId is not null && !string.Equals(rule.PrinterId, printerId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (rule.Method is not null && !string.Equals(rule.Method, method, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (rule.PathContains is not null &&
                !path.Contains(rule.PathContains, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Consume(rule);
        }

        return null;
    }

    public FaultRule? MatchWebSocket(string printerId, string? rpcMethod)
    {
        foreach (FaultRule rule in _rules.Values)
        {
            if (rule.Target != FaultTarget.WebSocket)
            {
                continue;
            }

            if (rule.PrinterId is not null && !string.Equals(rule.PrinterId, printerId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (rule.RpcMethod is not null &&
                !string.Equals(rule.RpcMethod, rpcMethod, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Consume(rule);
        }

        return null;
    }

    private FaultRule? Consume(FaultRule rule)
    {
        if (rule.Repeating)
        {
            return rule;
        }

        lock (rule)
        {
            if (rule.RemainingUses <= 0)
            {
                _rules.TryRemove(rule.Id, out _);
                return null;
            }

            rule.RemainingUses--;
            if (rule.RemainingUses <= 0)
            {
                _rules.TryRemove(rule.Id, out _);
            }

            return rule;
        }
    }
}
