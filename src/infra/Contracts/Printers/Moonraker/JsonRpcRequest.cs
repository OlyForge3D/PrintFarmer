using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

// JSON-RPC 2.0 models
public class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public object? Params { get; set; }

    [JsonPropertyName("id")]
    public object? Id { get; set; }
}
