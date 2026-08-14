namespace Farm.Moonraker.Emulator.Json;

/// <summary>Shared JSON conventions for both the REST and WebSocket Moonraker surfaces.</summary>
public static class MoonrakerJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Request DTOs (GcodeScriptRequest, PrintStartRequest, SpoolmanProxyRequest, ...)
        // use PascalCase C# property names but the wire format is Moonraker's exact
        // snake_case (e.g. "spool_id", "use_v2_response") — this policy converts between
        // them on both serialize and deserialize. Response payloads are built as
        // Dictionary<string, object?> with the snake_case keys written out literally, so
        // this policy has no effect on them (dictionary keys bypass naming policies).
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Writes a standard Moonraker <c>{"result": ...}</c> envelope.</summary>
    public static Task WriteResultAsync(HttpContext context, object? result, int statusCode = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new Dictionary<string, object?> { ["result"] = result }, Options));
    }

    /// <summary>Writes a Moonraker WebRequestError-style error body, matching what the real server sends for 4xx/5xx.</summary>
    public static Task WriteWebRequestErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["error"] = "WebRequestError", ["message"] = message },
            Options));
    }

    public static string BuildRpcResult(object? id, object? result) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result },
            Options);

    public static string BuildRpcError(object? id, int code, string message) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message },
            },
            Options);

    public static string BuildNotification(string method, object? paramsValue) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = paramsValue },
            Options);
}
