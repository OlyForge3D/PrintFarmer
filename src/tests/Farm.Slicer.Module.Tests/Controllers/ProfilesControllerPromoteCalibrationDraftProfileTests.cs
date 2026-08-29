using System;
using System.Linq;
using System.Reflection;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Api.Filters;
using Xunit;

namespace Farm.Slicer.Module.Tests.Controllers;

/// <summary>
/// Review finding (Bishop/Hicks, issue #2180): <see cref="ProfilesController.PromoteCalibrationDraftProfileAsync"/>
/// exists specifically to route calibration-completion promotion around
/// <see cref="Farm.Infrastructure.Authorization.InteractiveSessionRequirement"/>, which the
/// sibling <see cref="ProfilesController.UploadCustomProfileAsync"/> endpoint carries and which
/// unconditionally rejects desktop exchange tokens. Mirrors
/// <c>ProfilesControllerResolveProfileTests.ResolveProfileForModelAsync_IsGatedByCalibrationUpdate_NotAdmin</c>
/// to prove, via reflection rather than by reading the source, that the new endpoint is actually
/// gated by <see cref="PrintFarmerPermissions.Calibration.Update"/> and carries no interactive
/// session requirement of its own - a regression here would silently resurrect the exact bug this
/// endpoint exists to fix.
/// </summary>
public sealed class ProfilesControllerPromoteCalibrationDraftProfileTests
{
    [Fact]
    public void PromoteCalibrationDraftProfileAsync_IsGatedByCalibrationUpdate_NotInteractiveSessionOnly()
    {
        MethodInfo method = typeof(ProfilesController).GetMethod(nameof(ProfilesController.PromoteCalibrationDraftProfileAsync))
            ?? throw new InvalidOperationException($"{nameof(ProfilesController.PromoteCalibrationDraftProfileAsync)} not found via reflection");

        RequirePermissionAttribute methodAttribute = method.GetCustomAttributes<RequirePermissionAttribute>().SingleOrDefault()
            ?? throw new InvalidOperationException("Expected exactly one method-level RequirePermissionAttribute");

        Assert.Equal(PrintFarmerPermissions.Calibration.Update, methodAttribute.Permission);

        // The sibling UploadCustomProfileAsync endpoint this promotion path deliberately avoids
        // is the one gated by the interactive-session policy; this endpoint must not carry that
        // same class-level requirement in a way that would resurrect the bug (the class-level
        // RequirePermissionAttribute is Slicing.Submit, which the desktop calibration flow's scope
        // bundle already grants - it is not an interactive-session marker).
        RequirePermissionAttribute classAttribute = typeof(ProfilesController).GetCustomAttributes<RequirePermissionAttribute>().SingleOrDefault()
            ?? throw new InvalidOperationException("Expected exactly one class-level RequirePermissionAttribute");
        Assert.Equal(PrintFarmerPermissions.Slicing.Submit, classAttribute.Permission);
    }
}
