using System.Reflection;
using Farm.Slicer.Module.Contracts.Libraries;
using Farm.Slicers.OrcaSlicer.v2_3_1;

// Declare this assembly as a slicer library plugin
#pragma warning disable S101 // Class names required to match version numbering for plugin discovery
[assembly: SlicerPlugin(
    typeof(OrcaSlicerLibrary_v2_3_1),
    typeof(OrcaSlicerUIProvider_v2_3_1))]
#pragma warning restore S101
