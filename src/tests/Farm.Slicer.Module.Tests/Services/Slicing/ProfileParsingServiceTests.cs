using Farm.Web.Api.Services.Slicing;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services.Slicing;

public class ProfileParsingServiceTests
{
    private readonly IProfileParsingService _service = new ProfileParsingService();

    #region Null/Empty Input Tests

    [Fact]
    public void ParseAndPrepare_WithNullJson_ThrowsArgumentException()
    {
        // Act & Assert
        ArgumentException ex = Assert.Throws<ArgumentException>(() => _service.ParseAndPrepare(null!));
        Assert.Contains("Raw JSON is required", ex.Message);
    }

    [Fact]
    public void ParseAndPrepare_WithEmptyJson_ThrowsArgumentException()
    {
        // Act & Assert
        ArgumentException ex = Assert.Throws<ArgumentException>(() => _service.ParseAndPrepare(""));
        Assert.Contains("Raw JSON is required", ex.Message);
    }

    [Fact]
    public void ParseAndPrepare_WithWhitespaceOnlyJson_ThrowsArgumentException()
    {
        // Act & Assert
        ArgumentException ex = Assert.Throws<ArgumentException>(() => _service.ParseAndPrepare("   \n\t  "));
        Assert.Contains("Raw JSON is required", ex.Message);
    }

    #endregion

    #region Invalid JSON Tests

    [Fact]
    public void ParseAndPrepare_WithInvalidJson_ReturnsOpaque()
    {
        // Arrange
        string invalidJson = "{ this is not valid json }";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(invalidJson);

        // Assert
        Assert.Equal(invalidJson, result.SanitizedRawJson);
        Assert.Equal("{}", result.SettingsJson);
        Assert.NotEmpty(result.Hash);
        Assert.Equal(64, result.Hash.Length); // SHA256 is 64 hex chars
    }

    [Fact]
    public void ParseAndPrepare_WithMalformedJson_ReturnsOpaqueWithHash()
    {
        // Arrange
        string malformedJson = "{ \"key\": \"value\""; // Missing closing brace

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(malformedJson);

        // Assert
        Assert.Equal(malformedJson, result.SanitizedRawJson);
        Assert.Equal("{}", result.SettingsJson);
        Assert.NotEmpty(result.Hash);
    }

    #endregion

    #region Non-Object JSON Tests

    [Fact]
    public void ParseAndPrepare_WithJsonArray_ReturnsOpaque()
    {
        // Arrange
        string arrayJson = "[1, 2, 3]";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(arrayJson);

        // Assert
        Assert.Equal(arrayJson, result.SanitizedRawJson);
        Assert.Equal("{}", result.SettingsJson);
        Assert.NotEmpty(result.Hash);
    }

    [Fact]
    public void ParseAndPrepare_WithJsonString_ReturnsOpaque()
    {
        // Arrange
        string stringJson = "\"just a string\"";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(stringJson);

        // Assert
        Assert.Equal(stringJson, result.SanitizedRawJson);
        Assert.Equal("{}", result.SettingsJson);
        Assert.NotEmpty(result.Hash);
    }

    [Fact]
    public void ParseAndPrepare_WithJsonNumber_ReturnsOpaque()
    {
        // Arrange
        string numberJson = "42";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(numberJson);

        // Assert
        Assert.Equal(numberJson, result.SanitizedRawJson);
        Assert.Equal("{}", result.SettingsJson);
        Assert.NotEmpty(result.Hash);
    }

    #endregion

    #region Basic Object Tests

    [Fact]
    public void ParseAndPrepare_WithEmptyObject_ReturnsOrderedJson()
    {
        // Arrange
        string json = "{}";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert
        Assert.Equal("{}", result.SanitizedRawJson);
        Assert.Equal("{}", result.SettingsJson);
        Assert.NotEmpty(result.Hash);
    }

    [Fact]
    public void ParseAndPrepare_WithSimpleObject_ReturnsSanitized()
    {
        // Arrange
        string json = """{"name": "test", "value": 42}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert
        Assert.NotEmpty(result.SanitizedRawJson);
        // SettingsJson should include all properties (name and value)
        Assert.Contains("name", result.SettingsJson);
        Assert.Contains("value", result.SettingsJson);
        // Hash should be deterministic
        (string SanitizedRawJson, string SettingsJson, string Hash) result2 = _service.ParseAndPrepare(json);
        Assert.Equal(result.Hash, result2.Hash);
    }

    #endregion

    #region Volatile Key Removal Tests

    [Fact]
    public void ParseAndPrepare_RemovesVolatileKeys_LastModified()
    {
        // Arrange
        string json = """{"name": "test", "lastModified": "2025-12-09"}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert - lastModified should be excluded from sanitized
        Assert.DoesNotContain("lastModified", result.SanitizedRawJson);
        Assert.Contains("name", result.SanitizedRawJson);
    }

    [Fact]
    public void ParseAndPrepare_RemovesVolatileKeys_UUID()
    {
        // Arrange
        string json = """{"name": "test", "uuid": "550e8400-e29b-41d4-a716-446655440000"}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert
        Assert.DoesNotContain("uuid", result.SanitizedRawJson);
        Assert.Contains("name", result.SanitizedRawJson);
    }

    [Fact]
    public void ParseAndPrepare_RemovesVolatileKeys_CreationDate()
    {
        // Arrange
        string json = """{"name": "test", "creation_date": "2025-01-01", "modified": "2025-12-09"}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert
        Assert.DoesNotContain("creation_date", result.SanitizedRawJson);
        Assert.DoesNotContain("modified", result.SanitizedRawJson);
        Assert.Contains("name", result.SanitizedRawJson);
    }

    [Fact]
    public void ParseAndPrepare_RemovesVolatileKeys_AllVolatile()
    {
        // Arrange - object with only volatile keys
        string json = """{"lastModified": "2025-12-09", "uuid": "test", "timestamp": "2025-12-09T10:00:00Z"}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert
        Assert.Equal("{}", result.SanitizedRawJson);
        Assert.Equal("{}", result.SettingsJson);
    }

    #endregion

    #region Metadata Extraction Tests

    [Fact]
    public void ParseAndPrepare_ExtractsLayerHeight_Metadata()
    {
        // Arrange
        string json = """{"layer_height": 0.2, "other": "value"}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert - expects original key name "layer_height" not canonical "layerHeight"
        Assert.Contains("\"layer_height\":0.2", result.SettingsJson);
    }

    [Fact]
    public void ParseAndPrepare_ExtractsNozzleDiameter_Metadata()
    {
        // Arrange
        string json = """{"nozzle_diameter": 0.4, "other": "value"}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert - expects original key name "nozzle_diameter" not canonical "nozzleDiameter"
        Assert.Contains("\"nozzle_diameter\":0.4", result.SettingsJson);
    }

    [Fact]
    public void ParseAndPrepare_ExtractsFilamentType_Metadata()
    {
        // Arrange
        string json = """{"filament_type": "PLA", "other": "value"}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert - expects original key name "filament_type" not canonical "filamentMaterial"
        Assert.Contains("\"filament_type\":\"PLA\"", result.SettingsJson);
    }

    [Fact]
    public void ParseAndPrepare_ExtractsInfillDensity_Metadata()
    {
        // Arrange
        string json = """{"infill_density": 20, "other": "value"}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert - expects original key name "infill_density" not canonical "infillPercentage"
        Assert.Contains("\"infill_density\":20", result.SettingsJson);
    }

    [Fact]
    public void ParseAndPrepare_ExtractsSlicerVersion_Metadata()
    {
        // Arrange
        string json = """{"slicer_version": "3.16.0", "other": "value"}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert - expects original key name "slicer_version" not canonical "slicerVersion"
        Assert.Contains("\"slicer_version\":\"3.16.0\"", result.SettingsJson);
    }

    [Fact]
    public void ParseAndPrepare_ExtractsProfileType_Metadata()
    {
        // Arrange
        string json = """{"profile_type": "print", "other": "value"}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert - expects original key name "profile_type" not canonical "profileType"
        Assert.Contains("\"profile_type\":\"print\"", result.SettingsJson);
    }

    [Fact]
    public void ParseAndPrepare_ExtractsMultipleMetadata()
    {
        // Arrange
        string json = """{"layer_height": 0.2, "nozzle_diameter": 0.4, "filament_type": "PETG", "slicer_version": "3.16.0"}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert
        Assert.Contains("layer_height", result.SettingsJson);
        Assert.Contains("nozzle_diameter", result.SettingsJson);
        Assert.Contains("filament_type", result.SettingsJson);
        Assert.Contains("slicer_version", result.SettingsJson);
    }

    [Fact]
    public void ParseAndPrepare_MetadataOrdered_Alphabetically()
    {
        // Arrange
        string json = """{"slicer_version": "3.16.0", "layer_height": 0.2, "filament_type": "PLA"}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert - metadata keys should be in alphabetical order
        // filamentMaterial comes before layerHeight comes before slicerVersion
        int filamentPos = result.SettingsJson.IndexOf("filament_type");
        int layerPos = result.SettingsJson.IndexOf("layer_height");
        int slicerPos = result.SettingsJson.IndexOf("slicer_version");

        Assert.True(filamentPos < layerPos);
        Assert.True(layerPos < slicerPos);
    }

    [Fact]
    public void ParseAndPrepare_IncludesAllProperties_InSettingsJson()
    {
        // Arrange
        string json = """{"layer_height": [0.2, 0.3], "nozzle_diameter": 0.4, "nested": {"key": "value"}}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert - all properties including arrays and nested objects should be in settings
        Assert.Contains("nozzle_diameter", result.SettingsJson);
        // layer_height array should be included as JSON string
        Assert.Contains("layer_height", result.SettingsJson);
        // nested object should be included as JSON string  
        Assert.Contains("nested", result.SettingsJson);
    }

    [Fact]
    public void ParseAndPrepare_PreservesNonMetadataKeys_InSanitized()
    {
        // Arrange
        string json = """{"layer_height": 0.2, "custom_setting": "value", "other": 123}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert - all non-volatile keys should be in sanitized
        Assert.Contains("custom_setting", result.SanitizedRawJson);
        Assert.Contains("other", result.SanitizedRawJson);
        Assert.Contains("layer_height", result.SanitizedRawJson);
    }

    #endregion

    #region Deterministic Ordering Tests

    [Fact]
    public void ParseAndPrepare_OrdersKeysAlphabetically_Sanitized()
    {
        // Arrange
        string json = """{"z_key": 1, "a_key": 2, "m_key": 3}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert - keys should be in alphabetical order in result
        int aPos = result.SanitizedRawJson.IndexOf("a_key");
        int mPos = result.SanitizedRawJson.IndexOf("m_key");
        int zPos = result.SanitizedRawJson.IndexOf("z_key");

        Assert.True(aPos < mPos);
        Assert.True(mPos < zPos);
    }

    [Fact]
    public void ParseAndPrepare_ProducesDeterministic_Hash()
    {
        // Arrange
        string json = """{"b_key": 2, "a_key": 1, "c_key": 3}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result1 = _service.ParseAndPrepare(json);
        (string SanitizedRawJson, string SettingsJson, string Hash) result2 = _service.ParseAndPrepare("""{"a_key": 1, "b_key": 2, "c_key": 3}""");
        (string SanitizedRawJson, string SettingsJson, string Hash) result3 = _service.ParseAndPrepare("""{"c_key": 3, "a_key": 1, "b_key": 2}""");

        // Assert - same logical content should produce same hash regardless of order
        Assert.Equal(result1.Hash, result2.Hash);
        Assert.Equal(result2.Hash, result3.Hash);
    }

    #endregion

    #region Complex Object Tests

    [Fact]
    public void ParseAndPrepare_WithComplexProfile_ExtractsMetadataAndSanitizes()
    {
        // Arrange
        string complexProfile = """
        {
            "layer_height": 0.2,
            "nozzle_diameter": 0.4,
            "filament_type": "PLA",
            "infill_density": 20,
            "slicer_version": "3.16.0",
            "profile_type": "print",
            "uuid": "550e8400-e29b-41d4-a716-446655440000",
            "lastModified": "2025-12-09T10:00:00Z",
            "custom_setting": "value",
            "build_volume": {"x": 250, "y": 210, "z": 210},
            "max_speed": 200
        }
        """;

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(complexProfile);

        // Assert - metadata should contain recognized keys
        Assert.Contains("layer_height", result.SettingsJson);
        Assert.Contains("nozzle_diameter", result.SettingsJson);
        Assert.Contains("filament_type", result.SettingsJson);
        Assert.Contains("infill_density", result.SettingsJson);
        Assert.Contains("slicer_version", result.SettingsJson);
        Assert.Contains("profile_type", result.SettingsJson);

        // Volatile keys should be excluded
        Assert.DoesNotContain("uuid", result.SanitizedRawJson);
        Assert.DoesNotContain("lastModified", result.SanitizedRawJson);

        // Non-volatile, non-metadata keys should be preserved
        Assert.Contains("custom_setting", result.SanitizedRawJson);
        Assert.Contains("build_volume", result.SanitizedRawJson);
        Assert.Contains("max_speed", result.SanitizedRawJson);

        // Hash should be non-empty and valid
        Assert.NotEmpty(result.Hash);
        Assert.Equal(64, result.Hash.Length);
    }

    [Fact]
    public void ParseAndPrepare_WithWhitespaceVariations_ProducesSameHash()
    {
        // Arrange
        string formatted = """
        {
            "layer_height": 0.2,
            "custom": "value"
        }
        """;

        string compact = """{"layer_height": 0.2, "custom": "value"}""";
        string minified = """{"layer_height":0.2,"custom":"value"}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result1 = _service.ParseAndPrepare(formatted);
        (string SanitizedRawJson, string SettingsJson, string Hash) result2 = _service.ParseAndPrepare(compact);
        (string SanitizedRawJson, string SettingsJson, string Hash) result3 = _service.ParseAndPrepare(minified);

        // Assert - all should produce same hash (deterministic output)
        Assert.Equal(result1.Hash, result2.Hash);
        Assert.Equal(result2.Hash, result3.Hash);
    }

    #endregion

    #region String Trimming Tests

    [Fact]
    public void ParseAndPrepare_WithLeadingWhitespace_TrimmedInOpaque()
    {
        // Arrange
        string json = "   {invalid json}";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert
        Assert.Equal("{invalid json}", result.SanitizedRawJson);
    }

    [Fact]
    public void ParseAndPrepare_WithTrailingWhitespace_TrimmedInOpaque()
    {
        // Arrange
        string json = "{invalid json}   ";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert
        Assert.Equal("{invalid json}", result.SanitizedRawJson);
    }

    #endregion

    #region Hash Consistency Tests

    [Fact]
    public void ParseAndPrepare_HashIsHexadecimal()
    {
        // Arrange
        string json = """{"key": "value"}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert
        Assert.NotEmpty(result.Hash);
        Assert.True(result.Hash.All(c => "0123456789abcdef".Contains(c)), "Hash should be lowercase hexadecimal");
    }

    [Fact]
    public void ParseAndPrepare_HashLength_IsSHA256()
    {
        // Arrange
        string json = """{"key": "value"}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert - SHA256 hash is always 64 hex characters
        Assert.Equal(64, result.Hash.Length);
    }

    [Fact]
    public void ParseAndPrepare_DifferentContent_ProducesDifferentHash()
    {
        // Arrange
        string json1 = """{"key": "value1"}""";
        string json2 = """{"key": "value2"}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result1 = _service.ParseAndPrepare(json1);
        (string SanitizedRawJson, string SettingsJson, string Hash) result2 = _service.ParseAndPrepare(json2);

        // Assert
        Assert.NotEqual(result1.Hash, result2.Hash);
    }

    #endregion

    #region Metadata Type Handling Tests

    [Fact]
    public void ParseAndPrepare_MetadataWithStringValues()
    {
        // Arrange
        string json = """{"filament_type": "PLA", "slicer_version": "3.16.0"}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert - expects original key names
        Assert.Contains("\"filament_type\":\"PLA\"", result.SettingsJson);
        Assert.Contains("\"slicer_version\":\"3.16.0\"", result.SettingsJson);
    }

    [Fact]
    public void ParseAndPrepare_MetadataWithNumericValues()
    {
        // Arrange
        string json = """{"layer_height": 0.2, "nozzle_diameter": 0.4, "infill_density": 20}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert - expects original key names
        Assert.Contains("\"layer_height\":0.2", result.SettingsJson);
        Assert.Contains("\"nozzle_diameter\":0.4", result.SettingsJson);
        Assert.Contains("\"infill_density\":20", result.SettingsJson);
    }

    [Fact]
    public void ParseAndPrepare_MetadataWithBoolValues()
    {
        // Arrange
        string json = """{"supports_enabled": true}""";

        // Act
        (string SanitizedRawJson, string SettingsJson, string Hash) result = _service.ParseAndPrepare(json);

        // Assert - boolean should be preserved as non-metadata since it's not in metadata map
        Assert.Contains("supports_enabled", result.SanitizedRawJson);
    }

    #endregion
}
