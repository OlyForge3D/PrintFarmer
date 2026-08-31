using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Farm.Web.Api.Infrastructure.OpenApi;

/// <summary>
/// Removes nullable properties from an object schema's "required" list.
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
/// same option for MVC controllers), so any property whose CLR type is nullable is *always* omitted
/// from the wire when its value is null. <see cref="JsonPropertyInfo.IsGetNullable"/> reports exactly
/// that nullability (nullable reference-type annotation or <c>Nullable&lt;T&gt;</c>) as computed by
/// System.Text.Json's own reflection, using the same <see cref="System.Text.Json.JsonSerializerOptions"/>
/// that governs serialization — so it is the same signal the serializer itself relies on, not a
/// separate heuristic that could drift from it. A property that is nullable can therefore never be
/// truly "always present" on the wire, and must not appear in the schema's "required" list. This
/// transformer only ever *removes* names from "required"; it never adds one, so a non-nullable
/// property that is genuinely always present (including one carrying <c>[Required]</c> or the
/// C# <c>required</c> modifier — a repo-wide audit found no case where either is combined with a
/// nullable property type in <c>src/infra/Dtos/</c>) is left untouched.
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
            if (propertyInfo.IsGetNullable)
            {
                _ = required.Remove(propertyInfo.Name);
            }
        }

        return Task.CompletedTask;
    }
}
