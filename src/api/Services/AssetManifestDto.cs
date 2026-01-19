using System.Text.Json.Serialization;

namespace Farm.Web.Api.Services
{
#pragma warning disable SA1402 // File may only contain a single type
    public class AssetManifestDto
#pragma warning restore SA1402 // File may only contain a single type
    {
        [JsonPropertyName("manufacturers")]
        public List<ManufacturerAssetsDto> Manufacturers { get; set; } = new();
    }
}
