using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Farm.Web.Api.Infrastructure.OpenApi;

internal sealed class BearerSecuritySchemeTransformer(
    IAuthenticationSchemeProvider authenticationSchemeProvider) : IOpenApiDocumentTransformer
{
    public async Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        _ = cancellationToken;

        IEnumerable<AuthenticationScheme> schemes =
            await authenticationSchemeProvider.GetAllSchemesAsync();
        if (!schemes.Any(scheme => scheme.Name == "Bearer"))
        {
            return;
        }

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??=
            new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            In = ParameterLocation.Header,
            BearerFormat = "JWT",
            Description = "PrintFarmer JWT bearer access token.",
        };
        document.Components.SecuritySchemes["SlicerRegistryKey"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "X-Slicer-Api-Key",
            Description = "Shared key used only for slicer registry operations.",
        };
        document.Components.SecuritySchemes["SlicerServiceKey"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "X-Slicer-Service-Api-Key",
            Description = "Per-service key bound to the slicer service ID in the route.",
        };
        document.Components.SecuritySchemes["WorkerKey"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "X-Worker-Key",
            Description = "Shared worker key.",
        };
        document.Components.SecuritySchemes["WorkerServiceId"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "X-Worker-Id",
            Description = "Registry-issued slicer service GUID used to resolve the worker identity.",
        };
    }
}

internal sealed class AuthorizationOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        IList<object> metadata = context.Description.ActionDescriptor.EndpointMetadata;
        bool requiresRegistryKey = metadata.Any(item => item.GetType().Name == "RequireSlicerApiKeyAttribute");
        bool requiresServiceKey = metadata.Any(item => item.GetType().Name == "RequireSlicerServiceApiKeyAttribute");
        bool requiresWorkerKey = metadata.Any(item => item.GetType().Name == "WorkerApiKeySecurityAttribute");
        if (requiresRegistryKey || requiresServiceKey || requiresWorkerKey)
        {
            AddApiKeySecurity(
                operation,
                context.Document!,
                requiresRegistryKey,
                requiresServiceKey,
                requiresWorkerKey);
            return Task.CompletedTask;
        }

        bool allowsAnonymous = metadata.OfType<IAllowAnonymous>().Any();
        bool requiresAuthorization = metadata.OfType<IAuthorizeData>().Any()
            || metadata.Any(item => item.GetType().Name == "RequirePermissionAttribute");
        if (allowsAnonymous || !requiresAuthorization)
        {
            return Task.CompletedTask;
        }

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = [],
        });

        operation.Responses ??= new OpenApiResponses();
        if (!operation.Responses.ContainsKey("401"))
        {
            operation.Responses["401"] = new OpenApiResponse
            {
                Description = "Authentication is required.",
            };
        }

        if (!operation.Responses.ContainsKey("403"))
        {
            operation.Responses["403"] = new OpenApiResponse
            {
                Description = "The authenticated identity is not authorized for this resource.",
            };
        }

        return Task.CompletedTask;
    }

    private static void AddApiKeySecurity(
        OpenApiOperation operation,
        OpenApiDocument document,
        bool requiresRegistryKey,
        bool requiresServiceKey,
        bool requiresWorkerKey)
    {
        var requirement = new OpenApiSecurityRequirement();
        if (requiresRegistryKey)
        {
            requirement[new OpenApiSecuritySchemeReference("SlicerRegistryKey", document)] = [];
        }

        if (requiresServiceKey)
        {
            requirement[new OpenApiSecuritySchemeReference("SlicerServiceKey", document)] = [];
        }

        if (requiresWorkerKey)
        {
            requirement[new OpenApiSecuritySchemeReference("WorkerKey", document)] = [];
            requirement[new OpenApiSecuritySchemeReference("WorkerServiceId", document)] = [];
        }

        operation.Security ??= [];
        operation.Security.Add(requirement);
        operation.Responses ??= new OpenApiResponses();
        if (!operation.Responses.ContainsKey("401"))
        {
            operation.Responses["401"] = new OpenApiResponse
            {
                Description = "The required API-key authentication headers are missing or invalid.",
            };
        }

        if (requiresWorkerKey && !operation.Responses.ContainsKey("403"))
        {
            operation.Responses["403"] = new OpenApiResponse
            {
                Description = "The authenticated worker does not own the addressed resource.",
            };
        }
    }
}
