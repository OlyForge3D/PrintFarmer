#!/bin/bash
set -e
cd "$(dirname "$0")"

TESTS=(
"Farm.Web.Api.Tests.DatabaseEntityTests.Model3D_ShouldCreateAndRetrieve_WithAllPropertiesAsync"
"Farm.Web.Api.Tests.DatabaseEntityTests.SlicerProfile_ShouldCreateAndRetrieve_WithAllPropertiesAsync"
"Farm.Web.Api.Tests.DatabaseEntityTests.PrintJob_ShouldCreateAndRetrieve_WithAllPropertiesAsync"
"Farm.Web.Api.Tests.DatabaseEntityTests.PrinterCapabilities_ShouldCreateAndRetrieve_WithAllPropertiesAsync"
"Farm.Web.Api.Tests.DatabaseEntityTests.SlicerProfile_ShouldSupportAllSlicerTypesAsync(slicerType: PrusaSlicer)"
"Farm.Web.Api.Tests.DatabaseEntityTests.SlicerProfile_ShouldSupportAllSlicerTypesAsync(slicerType: OrcaSlicer)"
"Farm.Web.Api.Tests.DatabaseEntityTests.SlicerProfile_ShouldSupportAllSlicerTypesAsync(slicerType: Cura)"
"Farm.Web.Api.Tests.DatabaseEntityTests.SlicerProfile_ShouldSupportAllSlicerTypesAsync(slicerType: SuperSlicer)"
"Farm.Web.Api.Tests.DatabaseEntityTests.SlicerProfile_ShouldSupportAllQualityLevelsAsync(quality: Draft)"
"Farm.Web.Api.Tests.DatabaseEntityTests.SlicerProfile_ShouldSupportAllQualityLevelsAsync(quality: Standard)"
"Farm.Web.Api.Tests.DatabaseEntityTests.SlicerProfile_ShouldSupportAllQualityLevelsAsync(quality: Fine)"
"Farm.Web.Api.Tests.DatabaseEntityTests.Model3D_ShouldSupportAllFileFormatsAsync(format: STL)"
"Farm.Web.Api.Tests.DatabaseEntityTests.Model3D_ShouldSupportAllFileFormatsAsync(format: TMF)"
"Farm.Web.Api.Tests.DatabaseEntityTests.Model3D_ShouldSupportAllFileFormatsAsync(format: OBJ)"
"Farm.Web.Api.Tests.DatabaseEntityTests.Model3D_ShouldSupportAllFileFormatsAsync(format: PLY)"
"Farm.Web.Api.Tests.DatabaseEntityTests.Model3D_ShouldSupportAllFileFormatsAsync(format: STEP)"
"Farm.Web.Api.Tests.DatabaseEntityTests.PrintJob_ShouldSupportAllStatuses_BatchedLoop"
"Farm.Web.Api.Tests.DatabaseEntityTests.SlicerProfile_ShouldSupportPrinterModelAssociationAsync"
"Farm.Web.Api.Tests.DatabaseEntityTests.SlicerProfile_ShouldSupportSpecificPrinterAssociationAsync"
"Farm.Web.Api.Tests.DatabaseEntityTests.DatabaseContext_ShouldHandleComplexRelationshipsAsync"
"Farm.Web.Api.Tests.DiscoveryExclusionIntegrationTests.Streaming_discovery_should_exclude_already_added_printerAsync"
"Farm.Web.Api.Tests.DiscoverySignalRIntegrationTests.DiscoveryProgress_event_should_include_new_fieldsAsync"
"Farm.Web.Api.Tests.DiscoverySignalRIntegrationTests.DiscoveryProgress_event_should_set_autoDetected_true_when_networks_auto_detectedAsync"
)

for TEST in "${TESTS[@]}"; do
  echo "\n===== Running: $TEST ====="
  dotnet test ./src/farm-web.sln --filter "FullyQualifiedName=$TEST" --logger "trx;LogFileName=TestResults_$TEST.trx" || {
    echo "Test $TEST failed or hung."
    exit 1
  }
done
