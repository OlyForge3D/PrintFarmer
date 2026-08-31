using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Farm.Web.Api.Infrastructure.OpenApi;

/// <summary>
/// Adds the missing <c>"type"</c> keyword to every enum component schema whose only documented
/// shape is an <c>"enum"</c> token array.
/// </summary>
/// <remarks>
/// <para>
/// Issue #2261/#2282: this is a confirmed .NET framework limitation in the native minimal-API
/// OpenAPI generator (<c>Microsoft.AspNetCore.OpenApi</c>), not something specific to this
/// codebase -- see
/// <see href="https://github.com/dotnet/aspnetcore/issues/61303">dotnet/aspnetcore#61303</see> and
/// <see href="https://github.com/dotnet/aspnetcore/issues/62022">#62022</see>. When an enum is
/// serialized as a string via a global <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>
/// (registered on both <c>ConfigureHttpJsonOptions</c> and MVC's <c>AddJsonOptions</c> in
/// <c>Program.cs</c>), the generator's schema exporter recognizes the converter well enough to
/// populate the <c>enum</c> array with the real wire tokens, but never sets the schema's own
/// <c>type</c> keyword -- leaving the schema underspecified relative to a client generator that
/// expects <c>type</c> to be present alongside <c>enum</c>. <c>OpenApiEnumFidelityTests</c>'s
/// document-wide sweep flags essentially every non-<c>[Flags]</c>, standard-converter enum
/// reachable from <c>components.schemas</c> for exactly this reason, once #2282 removed that
/// test's blocking <c>Skip</c>.
/// </para>
/// <para>
/// A component schema shared by both a nullable and a non-nullable usage of the same enum type
/// (e.g. <c>ApiKeyPurpose</c>, referenced non-nullably by <c>ApiKey.Purpose</c> and nullably by an
/// optional query parameter) has its <c>enum</c> array already carrying a literal JSON
/// <c>null</c> entry alongside the real string tokens -- the generator's existing (if
/// <c>type</c>-omitting) handling of that merge is correct and is left untouched here; this
/// transformer only adds <see cref="JsonSchemaType.Null"/> to the <c>type</c> flags alongside
/// <see cref="JsonSchemaType.String"/> when it detects that literal <c>null</c> entry, so the type
/// declaration matches the enum array's own shape instead of contradicting it.
/// </para>
/// <para>
/// Deliberately narrow to the specific gap this test cares about: it only sets <c>type</c> when
/// the schema already carries a non-empty <c>enum</c> array (i.e., the exporter already resolved
/// real string tokens for it) but no <c>type</c> at all -- it never invents or corrects the
/// <c>enum</c> array's own contents, and it never overwrites a <c>type</c> another transformer
/// (such as <see cref="CustomConverterEnumSchemaTransformer"/>, which independently derives both
/// <c>type</c> and <c>enum</c> for the two enums the exporter can't resolve at all) already set.
/// <c>[Flags]</c> enums are intentionally left untouched: they may legitimately serialize a
/// combined, comma-separated value that a plain <c>type: string</c> with a fixed <c>enum</c> array
/// does not fully describe, and <c>OpenApiEnumFidelityTests</c> itself excludes them from its
/// strict comparison for the same reason.
/// </para>
/// </remarks>
/// <para>
/// A component schema can first be visited via a <c>Nullable&lt;TEnum&gt;</c>
/// <see cref="OpenApiSchemaTransformerContext.JsonTypeInfo"/> (from the nullable usage site), whose
/// <see cref="Type.IsEnum"/> is <see langword="false"/> even though the underlying enum is what the
/// schema actually describes -- so the enum type is unwrapped via
/// <see cref="Nullable.GetUnderlyingType(Type)"/> before the eligibility checks below, otherwise a
/// shared nullable/non-nullable schema like <c>ApiKeyPurpose</c> would never get its <c>type</c> set.
/// </para>
internal sealed class EnumSchemaTypeStringTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        Type clrType = context.JsonTypeInfo.Type;
        Type enumType = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (!enumType.IsEnum ||
            Attribute.IsDefined(enumType, typeof(FlagsAttribute)) ||
            schema.Type is not null ||
            schema.Enum is not { Count: > 0 } enumValues)
        {
            return Task.CompletedTask;
        }

        JsonSchemaType type = JsonSchemaType.String;
        if (enumValues.Any(value => value is null))
        {
            type |= JsonSchemaType.Null;
        }

        schema.Type = type;

        return Task.CompletedTask;
    }
}
