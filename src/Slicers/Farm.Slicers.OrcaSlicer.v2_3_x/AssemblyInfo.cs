using System.Reflection;
using Farm.Slicers.OrcaSlicer.v2_3_x;
using Farm.Web.Shared.Contracts.Slicing.Libraries;

// Declare this assembly as a slicer library plugin
[assembly: SlicerPlugin(
    typeof(OrcaSlicerLibrary_v2_3_x),
    typeof(OrcaSlicerUIProvider_v2_3_x)
)]
