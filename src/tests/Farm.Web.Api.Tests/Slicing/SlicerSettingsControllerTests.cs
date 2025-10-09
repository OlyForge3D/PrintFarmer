// LEGACY TESTS DISABLED: SlicerSettingsController was consolidated into UnifiedSettingsController
// These tests are no longer needed since slicer settings are now handled by the unified settings system.
// See UnifiedSettingsController (/api/settings) for the consolidated settings API.

using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Tests.Slicing;

public class SlicerSettingsControllerTests
{
    [Fact]
    public void LegacyController_Removed_TestsDisabled()
    {
        // SlicerSettingsController was consolidated into UnifiedSettingsController
        // All slicer settings functionality is now available via /api/settings
        Assert.True(true, "Legacy SlicerSettingsController tests disabled - functionality moved to UnifiedSettingsController");
    }
}
