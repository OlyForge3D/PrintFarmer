// Test-support types moved to Farm.Testing.Shared (issue #2032): TestAuthHandler, TestPaths,
// TestSqlitePragmaEnforcer, BlockOutboundHttpFilter/BlockingOutboundHandler, TestTimingAttribute,
// and TimingReportingTestFramework. Global-using them here avoids touching every one of this
// project's existing call sites individually.
global using Farm.Testing.Shared;
