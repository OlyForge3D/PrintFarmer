using System;
using System.Linq;
using System.Reflection;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Api.Filters;
using Xunit;

namespace Farm.Slicer.Module.Tests.Controllers;

/// <summary>
/// Issue #2203: <see cref="ProfilesController.DeleteCustomProfileAsync"/> exists to let a
/// non-admin caller delete their own custom profile without <c>slicer_engines:admin</c>, the
/// same way <see cref="ProfilesController.PromoteCalibrationDraftProfileAsync"/> (#2180) and
/// <see cref="ProfilesController.UpdateCustomProfileAsync"/> (#2189) already avoid it. Mirrors
/// <c>ProfilesControllerPromoteCalibrationDraftProfileTests</c> to prove, via reflection rather
/// than by reading the source, that the new endpoint is gated by
/// <see cref="PrintFarmerPermissions.Calibration.Update"/> rather than <c>slicer_engines:admin</c>
/// - a regression here would silently reintroduce the farm_admin requirement this endpoint exists
/// to remove - and that the admin-only <see cref="ProfilesController.DeleteProfileAsync"/> and
/// <see cref="ProfilesController.BulkDeleteProfilesAsync"/> routes remain unaffected.
/// </summary>
public sealed class ProfilesControllerDeleteCustomProfileTests
{
    [Fact]
    public void DeleteCustomProfileAsync_IsGatedByCalibrationUpdate_NotAdmin()
    {
        MethodInfo method = typeof(ProfilesController).GetMethod(nameof(ProfilesController.DeleteCustomProfileAsync))
            ?? throw new InvalidOperationException($"{nameof(ProfilesController.DeleteCustomProfileAsync)} not found via reflection");

        RequirePermissionAttribute methodAttribute = method.GetCustomAttributes<RequirePermissionAttribute>().SingleOrDefault()
            ?? throw new InvalidOperationException("Expected exactly one method-level RequirePermissionAttribute");

        Assert.Equal(PrintFarmerPermissions.Calibration.Update, methodAttribute.Permission);
        Assert.NotEqual("slicer_engines:admin", methodAttribute.Permission);
    }

    [Fact]
    public void DeleteProfileAsync_And_BulkDeleteProfilesAsync_RemainAdminGated()
    {
        // Guards against a future edit that relaxes the existing admin-only single-delete or
        // bulk-delete routes as a side effect of adding the new owner-scoped route - the two must
        // stay independent.
        MethodInfo deleteMethod = typeof(ProfilesController).GetMethod(nameof(ProfilesController.DeleteProfileAsync))
            ?? throw new InvalidOperationException($"{nameof(ProfilesController.DeleteProfileAsync)} not found via reflection");
        MethodInfo bulkDeleteMethod = typeof(ProfilesController).GetMethod(nameof(ProfilesController.BulkDeleteProfilesAsync))
            ?? throw new InvalidOperationException($"{nameof(ProfilesController.BulkDeleteProfilesAsync)} not found via reflection");

        RequirePermissionAttribute deleteAttribute = deleteMethod.GetCustomAttributes<RequirePermissionAttribute>().SingleOrDefault()
            ?? throw new InvalidOperationException("Expected exactly one method-level RequirePermissionAttribute on DeleteProfileAsync");
        RequirePermissionAttribute bulkDeleteAttribute = bulkDeleteMethod.GetCustomAttributes<RequirePermissionAttribute>().SingleOrDefault()
            ?? throw new InvalidOperationException("Expected exactly one method-level RequirePermissionAttribute on BulkDeleteProfilesAsync");

        Assert.Equal("slicer_engines:admin", deleteAttribute.Permission);
        Assert.Equal("slicer_engines:admin", bulkDeleteAttribute.Permission);
    }
}
