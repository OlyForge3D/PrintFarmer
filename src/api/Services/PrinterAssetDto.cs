using System.Text.Json.Serialization;

namespace Farm.Web.Api.Services
{
    /// <summary>
    /// DTOs for asset responses
    /// </summary>
#pragma warning disable SA1402 // File may only contain a single type
#pragma warning disable SA1649 // File name should match first type name
    public class PrinterAssetDto
#pragma warning restore SA1649 // File name should match first type name
#pragma warning restore SA1402 // File may only contain a single type
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("cover")]
        public string? Cover { get; set; }

        [JsonPropertyName("bedTexture")]
        public string? BedTexture { get; set; }
    }
}
