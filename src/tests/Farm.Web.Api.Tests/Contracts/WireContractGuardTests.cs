using System.Text.Json;
using Farm.Testing.Shared;

namespace Farm.Web.Api.Tests.Contracts;

/// <summary>
/// Negative-control meta-tests for issue #2238's regression guard (<c>JsonContractAssertions</c>
/// / <c>WireContractFixtureWriter.CaptureOrVerifyAsync</c>). These do not exercise any
/// production endpoint: they load one real, already-checked-in corpus fixture, produce a
/// deliberately corrupted copy of it in memory, and assert that
/// <see cref="JsonContractAssertions.CompareStructurally"/> actually flags the corruption.
/// This is what proves — rather than assumes — that "a camelCase→snake_case swap and a
/// string-enum→numeric-enum swap must each fail at least one assertion" (per the issue's
/// acceptance criteria), without touching any production code.
/// </summary>
public sealed class WireContractGuardTests : IDisposable
{
    private readonly List<JsonDocument> _trackedDocuments = new();

    public void Dispose()
    {
        foreach (JsonDocument document in _trackedDocuments)
        {
            document.Dispose();
        }
    }
    /// <summary>
    /// Loads <c>tasks/tasks.populated.json</c> (a real fixture with camelCase properties) and
    /// renames one property to its snake_case equivalent. The structural diff must report both
    /// a missing camelCase property and an unexpected additional snake_case property.
    /// </summary>
    [Fact]
    public void CompareStructurally_CamelCaseRenamedToSnakeCase_ReportsDifference()
    {
        JsonElement expected = LoadFixture("tasks/tasks.populated.json");
        string corrupted = RenameProperty(expected, "anchorKind", "anchor_kind");
        using JsonDocument corruptedDocument = JsonDocument.Parse(corrupted);

        IReadOnlyList<string> differences = JsonContractAssertions.CompareStructurally(
            expected,
            corruptedDocument.RootElement,
            volatilePaths: new HashSet<string> { "$.id", "$.createdAt" });

        _ = differences.Should().NotBeEmpty("a camelCase-to-snake_case rename must be caught by the structural guard");
        _ = differences.Should().Contain(d => d.Contains("anchorKind") && d.Contains("missing"));
        _ = differences.Should().Contain(d => d.Contains("anchor_kind") && d.Contains("unexpected"));

        _ = Assert.Throws<JsonContractAssertionException>(() =>
            JsonContractAssertions.AssertStructurallyEqual(
                expected,
                corruptedDocument.RootElement,
                volatilePaths: new HashSet<string> { "$.id", "$.createdAt" }));
    }

    /// <summary>
    /// Loads the same fixture and swaps a string-enum token (<c>"status": "Pending"</c>) for its
    /// numeric ordinal (<c>"status": 0</c>). The structural diff must flag the value-kind
    /// mismatch (string vs. number) at that exact path.
    /// </summary>
    [Fact]
    public void CompareStructurally_StringEnumSwappedForNumericOrdinal_ReportsDifference()
    {
        JsonElement expected = LoadFixture("tasks/tasks.populated.json");
        string corrupted = ReplacePropertyWithNumber(expected, "status", 0);
        using JsonDocument corruptedDocument = JsonDocument.Parse(corrupted);

        IReadOnlyList<string> differences = JsonContractAssertions.CompareStructurally(
            expected,
            corruptedDocument.RootElement,
            volatilePaths: new HashSet<string> { "$.id", "$.createdAt" });

        _ = differences.Should().NotBeEmpty("a string-enum-to-numeric-ordinal swap must be caught by the structural guard");
        _ = differences.Should().Contain(d => d.Contains("$.status") && d.Contains("String") && d.Contains("Number"));

        _ = Assert.Throws<JsonContractAssertionException>(() =>
            JsonContractAssertions.AssertStructurallyEqual(
                expected,
                corruptedDocument.RootElement,
                volatilePaths: new HashSet<string> { "$.id", "$.createdAt" }));
    }

    /// <summary>Sanity check: comparing the real fixture against itself reports zero differences.</summary>
    [Fact]
    public void CompareStructurally_UnmodifiedFixtureAgainstItself_ReportsNoDifference()
    {
        JsonElement expected = LoadFixture("tasks/tasks.populated.json");
        using JsonDocument actualDocument = JsonDocument.Parse(expected.GetRawText());

        IReadOnlyList<string> differences = JsonContractAssertions.CompareStructurally(expected, actualDocument.RootElement);

        _ = differences.Should().BeEmpty();
    }

    private JsonElement LoadFixture(string relativePath)
    {
        string fullPath = Path.Join(WireContractCorpusPaths.ApiRoot, relativePath);
        string json = File.ReadAllText(fullPath);
        JsonDocument document = JsonDocument.Parse(json);
        _trackedDocuments.Add(document);
        return document.RootElement;
    }

    private static string RenameProperty(JsonElement source, string fromName, string toName)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in source.EnumerateObject())
            {
                property.WriteTo(writer, property.Name == fromName ? toName : property.Name);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ReplacePropertyWithNumber(JsonElement source, string propertyName, int replacementValue)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in source.EnumerateObject())
            {
                if (property.Name == propertyName)
                {
                    writer.WriteNumber(propertyName, replacementValue);
                }
                else
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>Extension used only by <see cref="WireContractGuardTests"/> to rewrite a property under a new name.</summary>
file static class JsonPropertyExtensions
{
    public static void WriteTo(this JsonProperty property, Utf8JsonWriter writer, string name)
    {
        writer.WritePropertyName(name);
        property.Value.WriteTo(writer);
    }
}
