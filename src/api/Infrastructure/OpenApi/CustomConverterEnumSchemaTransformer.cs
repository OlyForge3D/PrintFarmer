using System.Text.Json.Nodes;
using Farm.Infrastructure.Domain;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Farm.Web.Api.Infrastructure.OpenApi;

/// <summary>
/// Constrains the component schema for enums whose real wire representation comes from a
/// PROPERTY-level <c>[JsonConverter]</c> attribute rather than a type-level one or a converter
/// registered directly into <c>JsonSerializerOptions.Converters</c>.
/// </summary>
/// <remarks>
/// <para>
/// Issue #2261 finding 2: <see cref="UserTaskAnchorKind"/>/<see cref="UserTaskSourceKind"/> are
/// referenced by <c>UserTaskDto.AnchorKind</c>/<c>SourceKind</c> and
/// <c>ShiftPlanGroupDto.AnchorKind</c> via a PROPERTY-level <c>[JsonConverter]</c> attribute
/// (issue #2246) -- the only kind of override that outranks the global
/// <c>JsonStringEnumConverter</c> registered on both <c>ConfigureHttpJsonOptions</c> and MVC's
/// <c>AddJsonOptions</c> for a specific property's real wire tokens (lowercase camelCase, e.g.
/// <c>"unspecified"</c>). .NET's native OpenAPI schema generator builds a referenced enum type's
/// component schema once, keyed purely on the TYPE, using only a TYPE-level converter attribute
/// or an options-registered converter -- it never re-inspects the property that referenced the
/// type, so this property-level override is invisible to it. Left alone, the resulting component
/// schema (see <c>UserTaskAnchorKind</c>/<c>UserTaskSourceKind</c> in <c>components.schemas</c>)
/// carries no "type" and no "enum" keyword at all: strictly worse for a generated client than the
/// plain-integer case, since there is no documented shape whatsoever (characterized before this
/// fix by <c>TasksOpenApiSchemaTests.CreateTask_ResponseSchema_PropertyLevelConverterEnums_HaveNoTypeConstraintAtAll</c>).
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
