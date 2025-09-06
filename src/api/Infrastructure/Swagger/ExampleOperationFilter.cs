using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Farm.Web.Api.Infrastructure.Swagger;

/// <summary>
/// Propagates schema-level examples to operation request/response bodies when not explicitly set.
/// This keeps the UI consistent without manually assigning examples per action.
/// </summary>
public sealed class ExampleOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (operation.RequestBody != null)
        {
            foreach (var content in operation.RequestBody.Content.Values)
            {
                if (content.Example == null && content.Schema?.Example != null)
                {
                    content.Example = content.Schema.Example;
                }
            }
        }

        foreach (var response in operation.Responses.Values)
        {
            if (response.Content == null) continue;
            foreach (var content in response.Content.Values)
            {
                if (content.Example == null && content.Schema?.Example != null)
                {
                    content.Example = content.Schema.Example;
                }
            }
        }
    }
}