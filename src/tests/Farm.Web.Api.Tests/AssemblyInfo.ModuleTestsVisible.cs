using System.Runtime.CompilerServices;

// SecurityAuditControllerTests (moved to Farm.Modules.Identity.Tests as part of issue #2041)
// needs CustomWebApplicationFactory's internal configOverrides constructor to spin up the
// real host for per-endpoint auth-behaviour testing.
[assembly: InternalsVisibleTo("Farm.Modules.Identity.Tests")]
