using System.Net.Http;
using System.Text.Json;

var content = @"{""result"": {""state"": ""ready"", ""state_message"": ""Printer is ready"", ""hostname"": ""x400"", ""klipper_path"": ""/home/mks/klipper""}}";
var doc = JsonDocument.Parse(content);
var root = doc.RootElement;

if (root.TryGetProperty("result", out var resultElem))
{
    root = resultElem;
}

bool hasStateMessage = root.TryGetProperty("state_message", out _);
bool hasKlipperPath = root.TryGetProperty("klipper_path", out _);
bool hasHostname = root.TryGetProperty("hostname", out JsonElement hostnameElem);

Console.WriteLine($"hasStateMessage: {hasStateMessage}");
Console.WriteLine($"hasKlipperPath: {hasKlipperPath}");
Console.WriteLine($"hasHostname: {hasHostname}");

if (hostnameElem.ValueKind == JsonValueKind.String)
{
    Console.WriteLine($"hostname value: {hostnameElem.GetString()}");
}
