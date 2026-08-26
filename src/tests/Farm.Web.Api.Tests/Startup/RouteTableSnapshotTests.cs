using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Startup;

/// <summary>
/// Regression guardrail for epic #2019's module-decomposition phases (issue #2035, Phase 7).
///
/// <para>
/// Captures the full controller-action route table -- HTTP verb(s), attribute-route template,
/// and controller/action identity -- and asserts it against a checked-in snapshot
/// (<c>Startup/RouteTableSnapshot.txt</c>). Every subsequent phase that moves a controller into
/// a <c>Farm.Modules.*</c> assembly (phases 8-18) must leave this snapshot byte-identical: a
/// diff here means a route silently changed template, verb, or moved to a different
/// controller/action pair during a "seam only" refactor, which is exactly the class of
/// regression the module migration must never introduce.
/// </para>
/// <para>
/// Renaming a controller/action or intentionally changing a route requires regenerating the
/// snapshot deliberately (see <see cref="BuildRouteTable"/>) and reviewing the diff -- it must
/// never be regenerated reflexively to make a failing test pass.
/// </para>
/// </summary>
public sealed class RouteTableSnapshotTests
{
    private static readonly string SnapshotPath = Path.GetFullPath(
        Path.Join(AppContext.BaseDirectory, "..", "..", "..", "Startup", "RouteTableSnapshot.txt"));

    [Fact]
    public void ControllerActionRouteTable_MatchesCheckedInSnapshot()
    {
        using CustomWebApplicationFactory factory = new();

        IActionDescriptorCollectionProvider actionProvider =
            factory.Services.GetRequiredService<IActionDescriptorCollectionProvider>();

        string[] actual = BuildRouteTable(actionProvider);
        string[] expected = File.ReadAllLines(SnapshotPath);

        actual.Should().Equal(
            expected,
            "the controller-action route table must not change while Farm.Modules.Abstractions " +
            "lands the module host seam (issue #2035) -- if this is a deliberate route change " +
            "unrelated to the module seam, regenerate Startup/RouteTableSnapshot.txt and review " +
            "the diff carefully");
    }

    /// <summary>
    /// Builds the sorted, checked-in-snapshot line format: one line per controller action, each
    /// listing every HTTP verb it accepts, its attribute-route template, and its
    /// <c>Controller.Action</c> identity.
    /// </summary>
    private static string[] BuildRouteTable(IActionDescriptorCollectionProvider actionProvider)
    {
        return actionProvider.ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Select(action =>
            {
                string methods = string.Join(
                    "+",
                    action.ActionConstraints?
                        .OfType<HttpMethodActionConstraint>()
                        .SelectMany(c => c.HttpMethods)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(m => m, StringComparer.Ordinal)
                    ?? Enumerable.Empty<string>());
                if (methods.Length == 0)
                {
                    methods = "ANY";
                }

                string template = action.AttributeRouteInfo?.Template ?? string.Empty;
                string identity = $"{action.ControllerTypeInfo.FullName}.{action.MethodInfo.Name}";
                return $"{methods} /{template} -> {identity}";
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();
    }
}
