using System.Text.Json.Nodes;
using Farm.Infrastructure.Domain;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Farm.Web.Api.Infrastructure.OpenApi;

/// <summary>
/// Constrains the component schema for enums whose real wire representation comes from a custom
/// <see cref="System.Text.Json.Serialization.JsonConverter{T}"/> the native OpenAPI generator does
/// not know how to introspect, rather than the standard <c>JsonStringEnumConverter</c> it does.
/// </summary>
/// <remarks>
/// <para>
/// Issue #2261 finding 2: <see cref="UserTaskAnchorKind"/>/<see cref="UserTaskSourceKind"/> carry a
/// TYPE-level <c>[JsonConverter(typeof(UserTaskAnchorKindJsonConverter))]</c>/
/// <c>[JsonConverter(typeof(UserTaskSourceKindJsonConverter))]</c> attribute directly on the enum
/// declaration (issue #2246), and are additionally re-annotated with the same property-level
/// attribute at each reference site (<c>UserTaskDto.AnchorKind</c>/<c>SourceKind</c>,
/// <c>ShiftPlanGroupDto.AnchorKind</c>, <c>IUserTaskService</c>'s DTOs) -- but neither placement
/// helps here: .NET's native OpenAPI schema generator only special-cases the standard
/// <c>JsonStringEnumConverter</c>/<c>JsonStringEnumConverter&lt;T&gt;</c> (which it can enumerate
/// without invoking) when producing an enum's component schema. Any OTHER
/// <see cref="System.Text.Json.Serialization.JsonConverter{T}"/> implementation -- type-level or
/// property-level alike -- is opaque to it, because the generator has no way to enumerate the
/// converter's real output tokens (lowercase camelCase, e.g. <c>"unspecified"</c>) without
/// executing arbitrary converter code. Left alone, the resulting component schema (see
/// <c>UserTaskAnchorKind</c>/<c>UserTaskSourceKind</c> in <c>components.schemas</c>) carries no
/// "type" and no "enum" keyword at all: strictly worse for a generated client than the
/// plain-integer case, since there is no documented shape whatsoever (characterized before this
/// fix by <c>TasksOpenApiSchemaTests.CreateTask_ResponseSchema_PropertyLevelConverterEnums_AreDocumentedAsStringWithMatchingEnumTokens</c>,
/// whose name and body were updated in this same change to assert the corrected behavior).
/// </para>
/// <para>
/// Calls <see cref="UserTaskAnchorKindJsonConverter.ToWire"/>/<see cref="UserTaskSourceKindJsonConverter.ToWire"/>
/// directly rather than re-deriving the wire mapping here, so the documented schema can never
/// silently drift from the converter that actually serializes these properties.
/// </para>
/// </remarks>
internal sealed class CustomConverterEnumSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (context.JsonTypeInfo.Type == typeof(UserTaskAnchorKind))
        {
            Constrain(schema, Enum.GetValues<UserTaskAnchorKind>(), UserTaskAnchorKindJsonConverter.ToWire);
        }
        else if (context.JsonTypeInfo.Type == typeof(UserTaskSourceKind))
        {
            Constrain(schema, Enum.GetValues<UserTaskSourceKind>(), UserTaskSourceKindJsonConverter.ToWire);
        }

        return Task.CompletedTask;
    }

    private static void Constrain<TEnum>(OpenApiSchema schema, TEnum[] values, Func<TEnum, string> toWire)
        where TEnum : struct, Enum
    {
        schema.Type = JsonSchemaType.String;
        schema.Enum = values.Select(value => (JsonNode)JsonValue.Create(toWire(value))).ToList();
    }
}
