using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Contracts.Printers.Moonraker;

public class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("result")]
    public object? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }

    [JsonPropertyName("id")]
    public object? Id { get; set; }
}
