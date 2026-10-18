using System.Runtime.CompilerServices;
using Farm.Backend.Plugin.Core;

[assembly: BackendPlugin(4, "OctoPrint Backend Plugin", "1.0.0", Description = "Plugin for OctoPrint 3D printer management software")]
[assembly: InternalsVisibleTo("Farm.Web.Api.Tests")]
[assembly: InternalsVisibleTo("Farm.Backend.Plugins.Tests")]
