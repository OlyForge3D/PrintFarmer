using System.Reflection;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Settings;
using Farm.Settings;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

/// <summary>
/// Guards the invariant enforced by <c>SettingsService.GetAllMetadata()</c>: every serialized
/// public instance property on a settings class must carry <see cref="JsonPropertyNameAttribute"/>.
/// </summary>
/// <remarks>
/// The metadata builder is a <c>yield</c>-based iterator that throws
/// <see cref="System.InvalidOperationException"/> when it encounters a public property without
/// <c>[JsonPropertyName]</c> (and without <c>[JsonIgnore]</c>). Because that throw happens while
/// the <c>GET /api/settings/metadata</c> response is already streaming, it surfaces to the browser
/// as <c>ERR_INCOMPLETE_CHUNKED_ENCODING</c>, and — via <c>BuildKeyNameToClassNameMap()</c> — makes
/// every per-key <c>GET /api/settings/{key}</c> call return 400. A regression here breaks the entire
/// Settings page, so it is cheaper to fail this unit test than to debug a half-streamed response.
/// </remarks>
public class SettingsMetadataAttributeTests
{
    public static IEnumerable<object[]> SettingTypes()
    {
        foreach (Type type in typeof(SlicerSettings).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<AppSettingAttribute>() != null))
        {
            yield return new object[] { type };
        }
    }

    [Theory]
    [MemberData(nameof(SettingTypes))]
    public void EverySerializedSettingProperty_HasJsonPropertyName(Type settingType)
    {
        // Mirror SettingsService.GetAllMetadata(): consider only public instance properties that
        // are NOT [JsonIgnore]d — those are the ones serialized and surfaced as editable settings.
        PropertyInfo[] serializedProps = settingType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
            .ToArray();

        string[] missing = serializedProps
            .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() == null)
            .Select(p => p.Name)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"Settings class '{settingType.Name}' has serialized public propert{(missing.Length == 1 ? "y" : "ies")} " +
            $"without [JsonPropertyName]: {string.Join(", ", missing)}. Add [JsonPropertyName] (or [JsonIgnore] for " +
            "derived/computed state) so GetAllMetadata() does not throw mid-stream and break the Settings page.");
    }

    [Fact]
    public void SlicerSettings_EffectiveEnabledModes_IsJsonIgnored()
    {
        // The computed back-compat accessor must stay [JsonIgnore]d: it is derived state, not a
        // persisted/editable setting, and lacking [JsonPropertyName] it would otherwise break metadata.
        PropertyInfo prop = typeof(SlicerSettings).GetProperty(nameof(SlicerSettings.EffectiveEnabledModes))!;
        Assert.NotNull(prop.GetCustomAttribute<JsonIgnoreAttribute>());
    }
}
