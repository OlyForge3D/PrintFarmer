using System.Reflection;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Settings;
using Farm.Settings;
using Farm.Web.Api.Controllers;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Guards which settings sections reach <c>GET /api/settings/metadata</c>.
/// </summary>
/// <remarks>
/// <para>
/// The settings page is driven entirely by that response: a section absent from metadata does
/// not render, cannot be searched, and — because the attention layer derives "needs attention"
/// strictly from metadata — can never raise a warning. So an accidental omission is invisible.
/// </para>
/// <para>
/// Two sections are omitted <em>on purpose</em>. <see cref="TelegramSettings"/> and
/// <see cref="HomeAssistantSettings"/> each carry an encrypted credential as an ordinary
/// serialized property, so exposing them through the generic surface would ship the stored
/// ciphertext to any signed-in caller and let it be overwritten by a plain PUT. Both have a
/// dedicated admin controller that masks on read and encrypts on write, and both enforce their
/// own <c>Validate()</c> on save, so the configuration they guard cannot be left half-set.
/// </para>
/// <para>
/// The dangerous direction is therefore not "a section is missing" but "a section that should
/// have been hidden was not". <see cref="EverySecretBearingSettingsClass_IsHidden"/> is the gate
/// for that: add an encrypted field to any settings class and the build goes red until the class
/// is either blocklisted or the field is marked <c>[JsonIgnore]</c>.
/// </para>
/// </remarks>
public class SettingsMetadataCoverageTests
{
    /// <summary>Property names that indicate a stored credential rather than a user-facing value.</summary>
    private static readonly string[] SecretNameFragments =
    [
        "Encrypted", "Secret", "Password", "ApiKey", "AccessToken", "BotToken", "PrivateKey",
    ];

    private static IEnumerable<Type> AppSettingTypes() =>
        typeof(SlicerSettings).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<AppSettingAttribute>() != null);

    private static string KeyOf(Type t) => t.GetCustomAttribute<AppSettingAttribute>()!.Key;

    /// <summary>
    /// Serialized properties whose name and type mark them as a stored credential. Mirrors the
    /// property set <c>SettingsService.GetAllMetadata()</c> walks: public instance, not
    /// <c>[JsonIgnore]</c>d.
    /// </summary>
    /// <remarks>
    /// Restricted to <see cref="string"/> because a credential is stored as text. Without that
    /// restriction the name match alone flags policy flags whose names merely contain a secret
    /// word — <c>OctoPrintSettings.RequireApiKey</c> and <c>HashStoredApiKeys</c> are both
    /// <see cref="bool"/> and hold no secret.
    /// </remarks>
    private static string[] SecretProperties(Type settingType) =>
        settingType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string))
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
            .Where(p => SecretNameFragments.Any(f => p.Name.Contains(f, StringComparison.Ordinal)))
            .Select(p => p.Name)
            .ToArray();

    [Fact]
    public void FindsSettingsClassesToCheck()
    {
        // Without this every assertion below would be vacuously true.
        Assert.True(AppSettingTypes().Count() > 10);
    }

    [Fact]
    public void EverySecretBearingSettingsClass_IsHidden()
    {
        string[] leaking = AppSettingTypes()
            .Where(t => SecretProperties(t).Length > 0)
            .Where(t => !UnifiedSettingsController.SecretBearingSectionKeys.Contains(KeyOf(t)))
            .Select(t => $"{t.Name} ({string.Join(", ", SecretProperties(t))})")
            .OrderBy(s => s)
            .ToArray();

        Assert.True(
            leaking.Length == 0,
            "These settings classes expose a stored credential through the generic settings " +
            "surface, which serves it to any signed-in caller and lets a plain PUT overwrite it: " +
            $"{string.Join("; ", leaking)}. Either add the section key to " +
            "UnifiedSettingsController's blocklist and give it a dedicated admin controller, or " +
            "mark the property [JsonIgnore] if it is not persisted.");
    }

    [Fact]
    public void EveryHiddenSection_ActuallyHoldsASecret()
    {
        // Keeps the blocklist honest in the other direction. A key listed here that no longer
        // holds a credential is hiding a section from the settings page for no reason.
        string[] unjustified = UnifiedSettingsController.SecretBearingSectionKeys
            .Where(key =>
            {
                Type? type = AppSettingTypes().FirstOrDefault(t =>
                    string.Equals(KeyOf(t), key, StringComparison.OrdinalIgnoreCase));
                return type == null || SecretProperties(type).Length == 0;
            })
            .OrderBy(k => k)
            .ToArray();

        Assert.True(
            unjustified.Length == 0,
            $"Blocklisted section key(s) {string.Join(", ", unjustified)} either name no " +
            "[AppSetting] class at all or no longer carry a credential. Remove them so the " +
            "section shows up on the settings page again.");
    }

    [Fact]
    public void HiddenSections_EnforceTheirOwnValidation()
    {
        // The blocklist costs these sections the metadata-driven "needs attention" signal, so
        // IValidatableSetting is the only thing left preventing a half-configured integration.
        string[] unguarded = UnifiedSettingsController.SecretBearingSectionKeys
            .Select(key => AppSettingTypes().FirstOrDefault(t =>
                string.Equals(KeyOf(t), key, StringComparison.OrdinalIgnoreCase)))
            .Where(t => t != null && !typeof(IValidatableSetting).IsAssignableFrom(t))
            .Select(t => t!.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.True(
            unguarded.Length == 0,
            $"{string.Join(", ", unguarded)} is hidden from the settings page but does not " +
            "implement IValidatableSetting, so nothing stops it being saved half-configured.");
    }

    [Fact]
    public void OnlyDeliberatelyHiddenSections_AreAbsentFromTheSettingsSurface()
    {
        // The positive statement of the contract: every [AppSetting] class is visible unless it
        // was hidden on purpose. This is what fails if a future change adds a filter, a
        // registration gap, or another blocklist entry without a decision behind it.
        string[] hidden = AppSettingTypes()
            .Select(KeyOf)
            .Where(k => UnifiedSettingsController.SecretBearingSectionKeys.Contains(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(new[] { HomeAssistantSettings.SectionName, TelegramSettings.SectionName }, hidden);
    }
}
