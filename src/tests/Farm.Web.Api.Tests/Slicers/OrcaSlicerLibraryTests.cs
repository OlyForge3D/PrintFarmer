using System.Threading.Tasks;
using Farm.Slicers.OrcaSlicer.v2_3_1;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Slicers;

public class OrcaSlicerProfilesProviderTests
{
    [Fact]
    public async Task ListAndLookups_ReturnEmbeddedProfilesAndNullForMissing()
    {
        var provider = new OrcaSlicerProfilesProvider();

        var profiles = await provider.ListOfficialProfilesAsync();
        var missingProfile = await provider.GetProfileJsonAsync("missing-id");
        var universal = await provider.GetUniversalFilamentsAsync();

        profiles.Should().NotBeEmpty();
        profiles.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name));
        missingProfile.Should().BeNull();
        universal.Should().NotBeNull();
    }
}

public class OrcaSlicerAssetRegistryTests
{
    [Fact]
    public async Task AssetsAndStreams_ReturnNullWhenNotEmbedded()
    {
        var registry = new OrcaSlicerAssetRegistry();

        var asset = await registry.GetAssetAsync("unknown", "model");
        var assets = await registry.ListAssetsAsync();

        asset.Should().BeNull();
        assets.Should().BeEmpty();
        registry.GetBedModelStream("unknown", "model").Should().BeNull();
        registry.GetBedTextureStream("unknown", "model").Should().BeNull();
        registry.GetCoverImageStream("unknown", "model").Should().BeNull();
    }
}

public class OrcaSlicerLibraryTests
{
    [Fact]
    public async Task LibraryExposesProvidersAndValidatesConfig()
    {
        var library = new OrcaSlicerLibrary_v2_3_1();

        library.SlicerName.Should().Be("OrcaSlicer");
        library.SlicerVersion.Should().Be("2.3.1");
        library.SlicerType.Should().Be("OrcaSlicer");

        library.ProfilesProvider.Should().NotBeNull();
        library.AssetRegistry.Should().NotBeNull();

        var validation = await library.ValidateConfigAsync(new object());
        validation.Should().NotBeNull();
    }
}

public class OrcaSlicerUiProviderTests
{
    [Fact]
    public void UiProviderExposesMetadata()
    {
        var ui = new OrcaSlicerUIProvider_v2_3_1();

        ui.SlicerName.Should().Be("OrcaSlicer");
        ui.SlicerVersion.Should().Be("2.3.1");
        ui.HasBundleSupport.Should().BeTrue();
        ui.HasAssetCustomization.Should().BeTrue();
        ui.HasEngineSpecificSettings.Should().BeTrue();
        ui.GetDescription().Should().Contain("OrcaSlicer v2.3.1");
    }
}
