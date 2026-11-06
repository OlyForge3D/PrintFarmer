using Farm.Infrastructure.Settings;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Farm.Slicer.Module.Api.Filters;

/// <summary>
/// Applies the configured artifact ceiling before multipart model binding, rather than inheriting
/// Kestrel's 30 MB request limit. Files remain disk-buffered above the normal form memory threshold.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WorkerArtifactUploadLimitsAttribute : Attribute, IAuthorizationFilter, IResourceFilter, IOrderedFilter
{
    /// <summary>The supported file ceiling, below the gateway's 500 MiB request ceiling.</summary>
    public const long MaximumFileSizeBytes = 500_000_000;

    /// <summary>Bounded allowance for multipart headers and the digest/size/kind fields.</summary>
    public const long MultipartOverheadBytes = 1024 * 1024;

    /// <inheritdoc/>
    public int Order => 900;

    /// <inheritdoc/>
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        long maxFileBytes = GetMaximumFileBytes(context.HttpContext.RequestServices);
        if (context.HttpContext.Request.ContentLength > maxFileBytes + MultipartOverheadBytes)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status413PayloadTooLarge);
            return;
        }

        IHttpMaxRequestBodySizeFeature? feature =
            context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = maxFileBytes + MultipartOverheadBytes;
        }
    }

    /// <inheritdoc/>
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        long maxFileBytes = GetMaximumFileBytes(context.HttpContext.RequestServices);
        FormOptions defaults = context.HttpContext.RequestServices.GetRequiredService<IOptions<FormOptions>>().Value;
        context.HttpContext.Features.Set<IFormFeature>(new FormFeature(context.HttpContext.Request, new FormOptions
        {
            BufferBody = defaults.BufferBody,
            MemoryBufferThreshold = defaults.MemoryBufferThreshold,
            BufferBodyLengthLimit = defaults.BufferBodyLengthLimit,
            ValueCountLimit = defaults.ValueCountLimit,
            KeyLengthLimit = defaults.KeyLengthLimit,
            ValueLengthLimit = defaults.ValueLengthLimit,
            MultipartBoundaryLengthLimit = defaults.MultipartBoundaryLengthLimit,
            MultipartHeadersCountLimit = defaults.MultipartHeadersCountLimit,
            MultipartHeadersLengthLimit = defaults.MultipartHeadersLengthLimit,
            MultipartBodyLengthLimit = maxFileBytes,
        }));
    }

    /// <inheritdoc/>
    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }

    private static long GetMaximumFileBytes(IServiceProvider services)
    {
        long value = services.GetRequiredService<IOptions<ArtifactStorageSettings>>().Value.MaxFileSizeBytes;
        if (value <= 0 || value > MaximumFileSizeBytes)
        {
            throw new InvalidOperationException("ArtifactStorage:MaxFileSizeBytes must be between 1 and 500000000.");
        }

        return value;
    }
}
