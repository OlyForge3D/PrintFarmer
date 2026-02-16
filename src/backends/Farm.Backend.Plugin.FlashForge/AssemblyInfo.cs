using System.Runtime.CompilerServices;
using Farm.Backend.Plugin.Core;

[assembly: BackendPlugin(5, "FlashForge Backend Plugin", "1.0.0", Description = "Plugin for FlashForge 3D printers using proprietary TCP serial protocol")]
[assembly: InternalsVisibleTo("Farm.Web.Api.Tests")]
