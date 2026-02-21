using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Dtos.Assets
{
#pragma warning disable SA1402 // File may only contain a single type
    public class ManufacturerAssetsDto
#pragma warning restore SA1402 // File may only contain a single type
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("printers")]
        public List<PrinterAssetDto> Printers { get; set; } = new();
    }
}
