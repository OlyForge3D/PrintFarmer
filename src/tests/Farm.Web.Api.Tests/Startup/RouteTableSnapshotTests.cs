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
/// and assembly-qualified controller/action identity -- and asserts it against a checked-in
/// snapshot (<c>Startup/RouteTableSnapshot.txt</c>). Every subsequent phase that moves a
/// controller into a <c>Farm.Modules.*</c> assembly (phases 8-18) must leave this snapshot
/// byte-identical: a diff here means a route silently changed template, verb, or moved to a
/// different controller/action pair during a "seam only" refactor, which is exactly the class
/// of regression the module migration must never introduce. The identity includes the
/// declaring assembly's name (not just the controller's namespace-qualified type name) so a
/// future move that accidentally leaves a stale copy of a controller behind in the old
/// assembly, alongside the moved copy in the new one, produces two distinct lines instead of
/// silently collapsing to one.
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
    /// <c>Assembly::Controller.Action</c> identity. The assembly qualifier deliberately makes
    /// two identically-named controllers in different assemblies produce distinct lines (see
    /// class remarks); no <c>Distinct()</c> is applied afterward, so a genuine duplicate route
    /// registration -- which this format could otherwise mask -- instead surfaces as a real
    /// diff against the snapshot rather than being silently deduplicated away.
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
                string assemblyName = action.ControllerTypeInfo.Assembly.GetName().Name ?? "?";
                string identity = $"{assemblyName}::{action.ControllerTypeInfo.FullName}.{action.MethodInfo.Name}";
                return $"{methods} /{template} -> {identity}";
            })
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToArray();
    }
}
