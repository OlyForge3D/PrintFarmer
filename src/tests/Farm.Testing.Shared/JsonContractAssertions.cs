using System.Text.Json;

namespace Farm.Testing.Shared;

/// <summary>
/// Assertion helpers for wire-contract tests. Every assertion here operates on
/// <see cref="JsonElement"/> (parsed from real serialized bytes) — never on a deserialized CLR
/// object — because a CLR-object assertion cannot detect a serializer misconfiguration
/// (camelCase vs snake_case, string enum vs numeric enum, unexpected null-handling, etc.); it
/// can only detect that the deserializer papered over the wire shape.
/// </summary>
public static class JsonContractAssertions
{
    /// <summary>Asserts <paramref name="propertyName"/> is present with the given <see cref="JsonValueKind"/>.</summary>
    public static JsonElement AssertProperty(JsonElement obj, string propertyName, JsonValueKind expectedKind)
    {
        if (!obj.TryGetProperty(propertyName, out JsonElement value))
        {
            throw new JsonContractAssertionException($"Expected property '{propertyName}' to be present, but it was missing.");
        }

        if (value.ValueKind != expectedKind)
        {
            throw new JsonContractAssertionException(
                $"Expected property '{propertyName}' to have kind {expectedKind}, but found {value.ValueKind}.");
        }

        return value;
    }

    /// <summary>Asserts <paramref name="propertyName"/> is a string equal to the exact enum wire token (e.g. "Brass", not "0").</summary>
    public static void AssertEnumToken(JsonElement obj, string propertyName, string expectedToken)
    {
        JsonElement value = AssertProperty(obj, propertyName, JsonValueKind.String);
        string? actual = value.GetString();
        if (!string.Equals(actual, expectedToken, StringComparison.Ordinal))
        {
            throw new JsonContractAssertionException(
                $"Expected property '{propertyName}' to be the exact string enum token '{expectedToken}', but found '{actual}'.");
        }
    }

    /// <summary>Asserts <paramref name="propertyName"/> is absent from the object entirely (distinct from present-but-null).</summary>
    public static void AssertMissingKey(JsonElement obj, string propertyName)
    {
        if (obj.TryGetProperty(propertyName, out _))
        {
            throw new JsonContractAssertionException($"Expected property '{propertyName}' to be absent, but it was present.");
        }
    }

    /// <summary>Asserts <paramref name="propertyName"/> is present with an explicit JSON null (distinct from a missing key).</summary>
    public static void AssertExplicitNull(JsonElement obj, string propertyName)
        => AssertProperty(obj, propertyName, JsonValueKind.Null);

    /// <summary>Asserts <paramref name="propertyName"/> is a present, empty JSON array.</summary>
    public static void AssertEmptyCollection(JsonElement obj, string propertyName)
    {
        JsonElement value = AssertProperty(obj, propertyName, JsonValueKind.Array);
        if (value.GetArrayLength() != 0)
        {
            throw new JsonContractAssertionException(
                $"Expected property '{propertyName}' to be an empty array, but it had {value.GetArrayLength()} elements.");
        }
    }

    /// <summary>Asserts <paramref name="propertyName"/> is a present, non-empty JSON array.</summary>
    public static JsonElement AssertNonEmptyCollection(JsonElement obj, string propertyName)
    {
        JsonElement value = AssertProperty(obj, propertyName, JsonValueKind.Array);
        if (value.GetArrayLength() == 0)
        {
            throw new JsonContractAssertionException($"Expected property '{propertyName}' to be a non-empty array, but it was empty.");
        }

        return value;
    }

    /// <summary>
    /// Structurally compares two JSON trees (object property order does not matter; array
    /// element order does). Returns a human-readable diff, one entry per difference; an empty
    /// list means the trees are equivalent.
    /// </summary>
    /// <param name="expected">The checked-in corpus fixture.</param>
    /// <param name="actual">The live serialized payload.</param>
    /// <param name="path">The JSON Pointer-like path of the root element being compared; callers should leave this at its default.</param>
    /// <param name="volatilePaths">
    /// Exact dotted/indexed paths (e.g. <c>"$.checkedAt"</c>, <c>"$.subsystems[0].detail"</c>) whose
    /// leaf <em>value</em> is intentionally non-deterministic (timestamps, elapsed-time strings,
    /// generated GUIDs). For these paths only <see cref="JsonValueKind"/> is compared, never the
    /// value — this keeps the fixture a real regression guard for shape/naming/enum-token drift
    /// without making the test flaky on data that legitimately changes every run. Leave this empty
    /// (the default) for fully deterministic payloads.
    /// </param>
    public static IReadOnlyList<string> CompareStructurally(
        JsonElement expected,
        JsonElement actual,
        string path = "$",
        IReadOnlySet<string>? volatilePaths = null)
    {
        var differences = new List<string>();
        CompareInto(expected, actual, path, differences, volatilePaths ?? EmptyVolatilePaths);
        return differences;
    }

    /// <summary>Throws with a full diff summary if <paramref name="expected"/> and <paramref name="actual"/> are not structurally equal.</summary>
    /// <param name="expected">The checked-in corpus fixture.</param>
    /// <param name="actual">The live serialized payload.</param>
    /// <param name="volatilePaths">See <see cref="CompareStructurally"/> for the semantics of this parameter.</param>
    public static void AssertStructurallyEqual(JsonElement expected, JsonElement actual, IReadOnlySet<string>? volatilePaths = null)
    {
        IReadOnlyList<string> differences = CompareStructurally(expected, actual, "$", volatilePaths);
        if (differences.Count > 0)
        {
            throw new JsonContractAssertionException(
                "Serialized payload no longer matches the checked-in corpus fixture:" + Environment.NewLine +
                string.Join(Environment.NewLine, differences));
        }
    }

    private static readonly HashSet<string> EmptyVolatilePaths = [];

    private static void CompareInto(
        JsonElement expected,
        JsonElement actual,
        string path,
        List<string> differences,
        IReadOnlySet<string> volatilePaths)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            differences.Add($"{path}: expected kind {expected.ValueKind}, found {actual.ValueKind}");
            return;
        }

        if (volatilePaths.Contains(path))
        {
            // Caller declared this leaf non-deterministic (timestamp, elapsed time, generated id).
            // Kind equality (already checked above) is all that's asserted for it.
            return;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                CompareObjects(expected, actual, path, differences, volatilePaths);
                break;
            case JsonValueKind.Array:
                CompareArrays(expected, actual, path, differences, volatilePaths);
                break;
            case JsonValueKind.String:
                if (!string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal))
                {
                    differences.Add($"{path}: expected \"{expected.GetString()}\", found \"{actual.GetString()}\"");
                }

                break;
            case JsonValueKind.Number:
                if (expected.GetRawText() != actual.GetRawText())
                {
                    differences.Add($"{path}: expected {expected.GetRawText()}, found {actual.GetRawText()}");
                }

                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                // Kind equality (checked above) is sufficient for these leaf kinds.
                break;
        }
    }

    private static void CompareObjects(
        JsonElement expected,
        JsonElement actual,
        string path,
        List<string> differences,
        IReadOnlySet<string> volatilePaths)
    {
        var expectedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in expected.EnumerateObject())
        {
            _ = expectedNames.Add(property.Name);
            if (!actual.TryGetProperty(property.Name, out JsonElement actualValue))
            {
                differences.Add($"{path}.{property.Name}: expected property to be present, but it was missing");
                continue;
            }

            CompareInto(property.Value, actualValue, $"{path}.{property.Name}", differences, volatilePaths);
        }

        foreach (JsonProperty property in actual.EnumerateObject())
        {
            if (!expectedNames.Contains(property.Name))
            {
                differences.Add($"{path}.{property.Name}: unexpected additional property present");
            }
        }
    }

    private static void CompareArrays(
        JsonElement expected,
        JsonElement actual,
        string path,
        List<string> differences,
        IReadOnlySet<string> volatilePaths)
    {
        if (expected.GetArrayLength() != actual.GetArrayLength())
        {
            differences.Add(
                $"{path}: expected array of length {expected.GetArrayLength()}, found length {actual.GetArrayLength()}");
            return;
        }

        int index = 0;
        JsonElement.ArrayEnumerator expectedEnumerator = expected.EnumerateArray();
        JsonElement.ArrayEnumerator actualEnumerator = actual.EnumerateArray();
        while (expectedEnumerator.MoveNext() && actualEnumerator.MoveNext())
        {
            CompareInto(expectedEnumerator.Current, actualEnumerator.Current, $"{path}[{index}]", differences, volatilePaths);
            index++;
        }
    }
}

/// <summary>Thrown when a wire-contract assertion fails.</summary>
public sealed class JsonContractAssertionException : Exception
{
    public JsonContractAssertionException()
    {
    }

    public JsonContractAssertionException(string message)
        : base(message)
    {
    }

    public JsonContractAssertionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
