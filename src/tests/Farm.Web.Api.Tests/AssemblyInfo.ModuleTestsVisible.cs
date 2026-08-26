using System.Runtime.CompilerServices;

// SecurityAuditControllerTests (moved to Farm.Modules.Identity.Tests as part of issue #2041)
// needs CustomWebApplicationFactory's internal configOverrides constructor to spin up the
// real host for per-endpoint auth-behaviour testing.
[assembly: InternalsVisibleTo("Farm.Modules.Identity.Tests")]

// AdminDataControllerTests, UnifiedSettingsPerKeyPostTests, and
// UnifiedSettingsAnonymousAccessTests (moved to Farm.Modules.Administration.Tests as part of
// issue #2042) each define a nested Factory : CustomWebApplicationFactory subclass calling the
// internal configOverrides constructor to spin up the real host.
[assembly: InternalsVisibleTo("Farm.Modules.Administration.Tests")]
