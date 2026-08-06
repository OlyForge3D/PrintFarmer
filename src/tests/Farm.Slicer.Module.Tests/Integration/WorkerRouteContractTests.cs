using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Slicer.Module.Tests.Integration;

/// <summary>
/// Guards against #920: the worker (<c>HttpJobPollerService</c>, <c>HttpProgressReporter</c>)
/// calls hardcoded route strings that are not statically checked against the API. If a route
/// is renamed on one side and not the other, the mismatch was previously only discoverable at
/// runtime. This test enumerates the literal route templates the worker actually sends and
/// asserts each one resolves to a real registered ASP.NET Core action.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class WorkerRouteContractTests
{
    /// <summary>
    /// The exact route templates the worker composes, taken from the literal interpolated
    /// strings in <c>HttpJobPollerService.cs</c> and <c>HttpProgressReporter.cs</c>
    /// (worker-shared project), with route parameters normalized to their ASP.NET Core
    /// placeholder form so they can be matched against <see cref="ControllerActionDescriptor"/>
    /// attribute route templates.
    /// </summary>
    private static readonly IReadOnlyList<(string Method, string Template)> WorkerInvokedRoutes =
    [
        ("POST", "api/slice/claim"),
        ("POST", "api/slice/{id}/renew-lease"),
        ("POST", "api/slice/{id}/progress"),
        ("POST", "api/slice/{id}/fail"),
        ("POST", "api/slice/{id}/complete"),
        ("POST", "api/slice/{id}/artifacts"),
    ];

    [Fact(DisplayName = "Every worker-invoked route resolves to a real API action")]
    public void WorkerRoutes_AllResolveToRealActions()
    {
        using var factory = new CustomWebApplicationFactory();
        IActionDescriptorCollectionProvider actionProvider =
            factory.Services.GetRequiredService<IActionDescriptorCollectionProvider>();

        List<(string Method, string Template)> registered = actionProvider.ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .SelectMany(action => (action.ActionConstraints?
                    .OfType<Microsoft.AspNetCore.Mvc.ActionConstraints.HttpMethodActionConstraint>()
                    .SingleOrDefault()?.HttpMethods ?? [])
                .Select(method => (Method: method, Template: NormalizeTemplate(action.AttributeRouteInfo?.Template))))
            .Where(entry => entry.Template is not null)
            .Select(entry => (entry.Method, Template: entry.Template!))
            .ToList();

        foreach ((string method, string template) in WorkerInvokedRoutes)
        {
            bool exists = registered.Any(r =>
                string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Template, template, StringComparison.OrdinalIgnoreCase));

            exists.Should().BeTrue(
                $"the worker sends {method} {template}; no registered API action matches it. " +
                "If this route was intentionally renamed, update both the worker call site and this list.");
        }
    }

    /// <summary>
    /// Reduces an attribute route template like <c>api/slice/{id:guid}/progress</c> to
    /// <c>api/slice/{id}/progress</c> so route-constraint syntax doesn't defeat the match
    /// against the worker's plain interpolated-parameter templates.
    /// </summary>
    private static string? NormalizeTemplate(string? template)
    {
        if (template is null)
        {
            return null;
        }

        return Regex.Replace(template, @"\{(\w+)(:[^}]+)?\}", "{$1}");
    }
}
