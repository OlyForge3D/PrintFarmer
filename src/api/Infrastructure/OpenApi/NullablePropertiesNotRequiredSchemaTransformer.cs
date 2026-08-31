using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Farm.Web.Api.Infrastructure.OpenApi;

/// <summary>
/// Removes nullable properties from an object schema's "required" list, unless the property is
/// itself explicitly marked required (C# <c>required</c>/<c>[JsonRequired]</c>, a DataAnnotations
/// <c>[Required]</c> request-validation attribute, or forced to always serialize via
/// <c>[JsonIgnore(Condition = JsonIgnoreCondition.Never)]</c>).
/// </summary>
/// <remarks>
/// <para>
/// Issue #2273 (follow-up from #2261): .NET's native OpenAPI schema generator marks every
/// constructor parameter of a positional record as "required" in the generated schema unless the
/// parameter has a default value, regardless of nullable-reference-type annotation. A repo-wide
/// sweep of <c>src/infra/Dtos/</c> found 60 positional records with one or more nullable
/// constructor parameters that lack a default value — far more than the ~30 suspected in the
/// issue — so per-DTO wire-contract corpus proof (the #2261 <c>UserTaskDto</c> pattern) was not a
/// proportionate fix for every affected type. This transformer instead makes the schema agree
/// with the real, already-configured wire contract structurally, for every type, present and
/// future, rather than one corpus-sampled DTO at a time.
/// </para>
/// <para>
/// The correctness argument does not rely on sampling wire payloads per DTO: <c>ConfigureHttpJsonOptions</c>
/// in <c>Program.cs</c> sets <c>DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull</c> for
/// every type reachable from native OpenAPI (and <c>ControllerStartup.AddJsonOptions</c> sets the
/// same option for MVC controllers), so a nullable property is omitted from the wire when its value
/// is null <em>unless something explicitly overrides that default</em> -- see the two carve-outs
/// below, both of which are real, existing cases in this repo, not merely hypothetical. Subject to
/// those carve-outs, <see cref="JsonPropertyInfo.IsGetNullable"/> reports exactly that nullability
/// (nullable reference-type annotation or <c>Nullable&lt;T&gt;</c>) as computed by System.Text.Json's
/// own reflection, using the same <see cref="System.Text.Json.JsonSerializerOptions"/> that governs
/// serialization — so it is the same signal the serializer itself relies on, not a separate
/// heuristic that could drift from it.
/// </para>
/// <para>
/// <b>Carve-out 1 -- request validation.</b> This transformer is registered globally, so it applies
/// to request-body schemas too, not just response DTOs under <c>src/infra/Dtos/</c>. A nullable-typed
/// request property can still be genuinely mandatory: ASP.NET Core model validation enforces a
/// DataAnnotations <c>[Required]</c> attribute independently of System.Text.Json's own
/// (de)serialization nullability, most commonly on a nullable value type such as
/// <c>decimal?</c>/<c>Guid?</c> used specifically so "omitted" and "explicitly invalid" can be told
/// apart before <c>[Required]</c> rejects the omitted case with a 400 (e.g.
/// <c>ZOffsetSaveRequest.OffsetMm</c>, <c>AssignPrinterToLocationRequest.LocationId</c>). Removing
/// such a property from "required" would make the OpenAPI document (and any client generated from
/// it) claim omission is valid when the server will actually reject it.
/// </para>
/// <para>
/// <b>Carve-out 2 -- forced-always-emission response properties.</b> A property can override
/// <c>DefaultIgnoreCondition = WhenWritingNull</c> for itself with
/// <c>[JsonIgnore(Condition = JsonIgnoreCondition.Never)]</c>, making it always present on the wire
/// even when its value is <c>null</c> (e.g. <c>ToolheadDto.CumulativePrintHours</c>, whose #719
/// consumers rely on the deterministic shape to distinguish "not applicable" from "zero hours"). If
/// such a property were also given the C# <c>required</c> modifier or <c>[JsonRequired]</c> in the
/// future, it would be correct to keep it "required" — this transformer must not strip it.
/// </para>
/// <para>
/// This transformer only ever removes names from "required"; it never adds one, so a property that
/// falls under either carve-out — or that is simply non-nullable — is always left untouched.
/// </para>
/// </remarks>
internal sealed class NullablePropertiesNotRequiredSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (schema.Required is not { Count: > 0 } required
            || context.JsonTypeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return Task.CompletedTask;
        }

        foreach (JsonPropertyInfo propertyInfo in context.JsonTypeInfo.Properties)
        {
            if (propertyInfo.IsGetNullable && !IsExplicitlyRequired(propertyInfo))
            {
                _ = required.Remove(propertyInfo.Name);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// True when the property was explicitly asked to stay required or always-present, so this
    /// transformer must not strip it even though its CLR type is nullable: the JSON serialization
    /// contract itself (C# <c>required</c> modifier / <c>[JsonRequired]</c>, surfaced as
    /// <see cref="JsonPropertyInfo.IsRequired"/>), an independent ASP.NET Core model-validation
    /// <see cref="RequiredAttribute"/> on the underlying property/field (not something
    /// System.Text.Json's own nullability handling is aware of, since it governs request-body
    /// validation rather than (de)serialization), or a forced-always-emission
    /// <c>[JsonIgnore(Condition = JsonIgnoreCondition.Never)]</c> override of the shared
    /// <c>WhenWritingNull</c> default.
    /// </summary>
    private static bool IsExplicitlyRequired(JsonPropertyInfo propertyInfo)
    {
        if (propertyInfo.IsRequired)
        {
            return true;
        }

        if (propertyInfo.AttributeProvider is not { } attributeProvider)
        {
            return false;
        }

        if (attributeProvider.GetCustomAttributes(typeof(RequiredAttribute), inherit: true) is { Length: > 0 })
        {
            return true;
        }

        return attributeProvider.GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true)
            .OfType<JsonIgnoreAttribute>()
            .Any(a => a.Condition == JsonIgnoreCondition.Never);
    }
}
