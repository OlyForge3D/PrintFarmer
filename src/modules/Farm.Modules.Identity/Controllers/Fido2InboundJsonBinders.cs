using System.Text.Json;
using Fido2NetLib;
using Microsoft.AspNetCore.Http;
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
/// Deserializes the passkey login completion body directly with <see cref="Fido2InboundJsonOptions"/>,
/// bypassing the MVC input formatter (and its global enum converter) that <c>[FromBody]</c> would otherwise use.
/// The request's nested <c>assertionResponse</c> object is parsed manually because a top-level
/// <c>[FromBody]</c>/<c>[ModelBinder]</c> replacement is the only way to isolate the whole object graph in one
/// pass - property-level binders on a <c>[FromBody]</c>-bound complex type are never invoked. See
/// <see cref="Fido2InboundJsonOptions"/> for the full rationale (issue #2763).
/// </summary>
internal sealed class PasskeyLoginCompleteRequestModelBinder : IModelBinder
{
    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        HttpRequest request = bindingContext.HttpContext.Request;
        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(
                request.Body,
                options: default,
                cancellationToken: bindingContext.HttpContext.RequestAborted);

            JsonElement root = document.RootElement;

            string username = root.TryGetProperty("username", out JsonElement usernameElement) && usernameElement.ValueKind == JsonValueKind.String
                ? usernameElement.GetString() ?? string.Empty
                : string.Empty;

            AuthenticatorAssertionRawResponse? assertionResponse = root.TryGetProperty("assertionResponse", out JsonElement assertionElement)
                && assertionElement.ValueKind == JsonValueKind.Object
                    ? assertionElement.Deserialize<AuthenticatorAssertionRawResponse>(Fido2InboundJsonOptions.Options)
                    : null;

            bindingContext.Result = ModelBindingResult.Success(new PasskeyLoginCompleteRequest(username, assertionResponse));
        }
        catch (JsonException ex)
        {
            bindingContext.ModelState.TryAddModelError(bindingContext.ModelName, $"Invalid passkey login payload: {ex.Message}");
            bindingContext.Result = ModelBindingResult.Failed();
        }
    }
}
