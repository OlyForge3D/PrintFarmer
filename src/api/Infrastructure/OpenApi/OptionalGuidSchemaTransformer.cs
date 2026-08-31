using Farm.Infrastructure.Dtos.PartsInventory;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Farm.Web.Api.Infrastructure.OpenApi;

/// <summary>
/// Documents <see cref="OptionalGuid"/> (issue #2294) as a plain nullable UUID string rather than
/// the opaque, typeless component schema the native OpenAPI generator would otherwise produce for
/// it.
/// </summary>
/// <remarks>
/// <see cref="OptionalGuid"/> exists purely so <c>HarvestConflictResponse.GcodeFileId</c> can
/// distinguish "property entirely absent" from "property present with an explicit JSON
/// <see langword="null"/>" -- a real wire-format need (see that property's remarks) but an
/// implementation detail no API consumer should have to model as its own type. The generator has
/// no way to look inside <see cref="OptionalGuidJsonConverter"/> to learn that the wrapped value
/// serializes as either a GUID string or <see langword="null"/>, so left alone it emits a
/// <c>components.schemas.OptionalGuid</c> entry with no <c>type</c> keyword at all -- strictly
/// less useful to a generated client (including iOS) than a plain <c>Guid?</c> would have been.
/// This transformer corrects that one component schema in place to <c>{"type": ["string",
/// "null"], "format": "uuid"}</c>, matching exactly what <c>gcodeFileId</c> actually serializes as
/// on the wire. Existing <c>$ref</c> pointers at the property (e.g.
/// <c>HarvestConflictResponse.gcodeFileId</c>) are left untouched -- only the referenced
/// component's own body is replaced -- so a consumer resolving that <c>$ref</c> sees the correct
/// shape without this codebase needing to inline the schema at every reference site by hand.
/// </remarks>
internal sealed class OptionalGuidSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (context.JsonTypeInfo.Type == typeof(OptionalGuid))
        {
            schema.Type = JsonSchemaType.String | JsonSchemaType.Null;
            schema.Format = "uuid";
            schema.Properties = null;
            schema.Required = null;
            schema.AdditionalPropertiesAllowed = true;
            schema.AdditionalProperties = null;
        }

        return Task.CompletedTask;
    }
}
