using System.Runtime.CompilerServices;
using Farm.Backend.Plugin.Core;

[assembly: BackendPlugin(100, "TestEmulator Backend Plugin", "1.0.0", Description = "Fake backend plugin for Playwright E2E testing. Simulates printer behavior without real hardware.")]
[assembly: InternalsVisibleTo("Farm.Web.Api.Tests")]
[assembly: InternalsVisibleTo("Farm.Backend.Plugins.Tests")]
