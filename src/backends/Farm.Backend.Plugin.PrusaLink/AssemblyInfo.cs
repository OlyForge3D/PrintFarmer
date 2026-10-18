using System.Runtime.CompilerServices;
using Farm.Backend.Plugin.Core;

[assembly: BackendPlugin(2, "PrusaLink Backend Plugin", "1.0.0", Description = "Plugin for Prusa printers via PrusaLink API")]
[assembly: InternalsVisibleTo("Farm.Web.Api.Tests")]
[assembly: InternalsVisibleTo("Farm.Backend.Plugins.Tests")]
