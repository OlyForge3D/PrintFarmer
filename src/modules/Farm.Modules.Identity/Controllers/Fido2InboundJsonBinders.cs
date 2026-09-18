using System.Text.Json;
using Fido2NetLib;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Farm.Modules.Identity.Controllers;

/// <summary>
/// Isolated <see cref="JsonSerializerOptions"/> for deserializing Fido2NetLib WebAuthn request bodies
/// (<see cref="AuthenticatorAttestationRawResponse"/>, <see cref="AuthenticatorAssertionRawResponse"/>).
///
/// PrintFarmer's global MVC JSON options (see <c>ControllerStartup.AddPrintFarmerControllers</c>)
/// register a non-generic <c>JsonStringEnumConverter</c> for every controller-bound payload. System.Text.Json
/// resolves a converter for a type by checking <see cref="JsonSerializerOptions.Converters"/> before it
/// consults a <c>[JsonConverter]</c> attribute declared on the type itself, so that global converter always
/// wins over <c>PublicKeyCredentialType</c>'s own converter - which is what maps the WebAuthn wire value
/// <c>"public-key"</c> to <c>PublicKeyCredentialType.PublicKey</c> via <c>[EnumMember]</c>. The global
/// converter instead expects the literal .NET enum member name <c>"PublicKey"</c>, so every real browser
/// passkey ceremony was rejected (issue #2763).
///
/// These options add no converters, so System.Text.Json falls through to each Fido2NetLib type's own
/// <c>[JsonPropertyName]</c>/<c>[JsonConverter]</c> attributes exactly as the library authors intended,
/// without touching PrintFarmer's global serialization configuration used by every other controller.
/// </summary>
internal static class Fido2InboundJsonOptions
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// <see cref="ModelBinderAttribute"/> variant that keeps <see cref="ModelMetadata.BindingSource"/> reported
/// as <see cref="BindingSource.Body"/> (matching the <c>[FromBody]</c> parameter this replaces) instead of
/// falling back to <see cref="BindingSource.Custom"/>. <see cref="ModelBinderAttribute.BindingSource"/> only
/// has a protected setter, so it cannot be set as a named argument on the base attribute directly; this
/// keeps OpenAPI/Swagger generation and any other tooling that inspects binding source reporting the
/// parameter as a JSON request body, exactly as it did with <c>[FromBody]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property)]
internal sealed class Fido2InboundBodyModelBinderAttribute : ModelBinderAttribute
{
    public Fido2InboundBodyModelBinderAttribute(Type binderType)
    {
        BinderType = binderType;
        BindingSource = BindingSource.Body;
    }
}

/// <summary>
/// Deserializes the passkey registration completion body directly with <see cref="Fido2InboundJsonOptions"/>,
/// bypassing the MVC input formatter (and its global enum converter) that <c>[FromBody]</c> would otherwise use.
/// See <see cref="Fido2InboundJsonOptions"/> for the full rationale (issue #2763).
/// </summary>
internal sealed class AuthenticatorAttestationRawResponseModelBinder : IModelBinder
{
    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        HttpRequest request = bindingContext.HttpContext.Request;
        try
        {
            AuthenticatorAttestationRawResponse? value = await JsonSerializer.DeserializeAsync<AuthenticatorAttestationRawResponse>(
                request.Body,
                Fido2InboundJsonOptions.Options,
                bindingContext.HttpContext.RequestAborted);

            if (value is null)
            {
                bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, "The passkey attestation request body is required.");
                bindingContext.Result = ModelBindingResult.Failed();
                return;
            }

            bindingContext.Result = ModelBindingResult.Success(value);
        }
        catch (JsonException ex)
        {
            bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, $"Invalid passkey attestation payload: {ex.Message}");
            bindingContext.Result = ModelBindingResult.Failed();
        }
    }
}

/// <summary>
/// Wire envelope for the passkey login completion body, deserialized with
/// <see cref="Fido2InboundJsonOptions"/> so the nested <see cref="AuthenticatorAssertionRawResponse"/>.Type
/// resolves via Fido2NetLib's own attributes instead of PrintFarmer's global enum converter (issue #2763).
/// Deserializing the whole envelope in one pass (rather than hand-walking a <see cref="JsonDocument"/>)
/// keeps the case-insensitive property matching that <see cref="JsonSerializerDefaults.Web"/> provides, and
/// lets <see cref="JsonSerializer.DeserializeAsync{TValue}(System.IO.Stream, JsonSerializerOptions?, System.Threading.CancellationToken)"/>
/// reject a non-object JSON root (e.g. <c>[]</c>, a bare string/number, or <c>null</c>) as a <see cref="JsonException"/>
/// - rather than a <c>JsonElement.TryGetProperty</c> call throwing <see cref="InvalidOperationException"/>
/// on a non-object root and escaping as an unhandled 500.
/// </summary>
internal sealed record PasskeyLoginCompleteEnvelope(string? Username, AuthenticatorAssertionRawResponse? AssertionResponse);

/// <summary>
/// Deserializes the passkey login completion body directly with <see cref="Fido2InboundJsonOptions"/>,
/// bypassing the MVC input formatter (and its global enum converter) that <c>[FromBody]</c> would otherwise use.
/// See <see cref="Fido2InboundJsonOptions"/> and <see cref="PasskeyLoginCompleteEnvelope"/> for the full
/// rationale (issue #2763).
/// </summary>
internal sealed class PasskeyLoginCompleteRequestModelBinder : IModelBinder
{
    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        HttpRequest request = bindingContext.HttpContext.Request;
        try
        {
            PasskeyLoginCompleteEnvelope? envelope = await JsonSerializer.DeserializeAsync<PasskeyLoginCompleteEnvelope>(
                request.Body,
                Fido2InboundJsonOptions.Options,
                bindingContext.HttpContext.RequestAborted);

            if (envelope is null)
            {
                bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, "The passkey login request body is required.");
                bindingContext.Result = ModelBindingResult.Failed();
                return;
            }

            bindingContext.Result = ModelBindingResult.Success(
                new PasskeyLoginCompleteRequest(envelope.Username ?? string.Empty, envelope.AssertionResponse));
        }
        catch (JsonException ex)
        {
            bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, $"Invalid passkey login payload: {ex.Message}");
            bindingContext.Result = ModelBindingResult.Failed();
        }
    }
}
