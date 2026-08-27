// Test-support types live in Farm.Testing.Shared (issue #2032): TestAuthHandler, TestPaths,
// TestSqlitePragmaEnforcer, BlockOutboundHttpFilter/BlockingOutboundHandler, TestTimingAttribute,
// TimingReportingTestFramework, TestHelpers, and ProviderDatabaseTestCollection (the last two
// relocated here from Farm.Web.Api.Tests/TestInfrastructure in issue #2033, since they are pure
// Farm.Infrastructure helpers shared by both test assemblies, not Farm.Web.Api-specific).
// Global-using them here avoids touching every call site individually.
global using System.Net.Http.Json;
global using Farm.Settings;
global using Farm.Testing.Shared;
global using FluentAssertions;
global using Xunit;
