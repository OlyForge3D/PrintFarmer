namespace Farm.Web.Api.Services.Calibration.Generation;

/// <summary>
/// Stable, client-safe rejection codes emitted by the calibration generation services.
/// </summary>
/// <remarks>
/// Codes are snake_case so they match the existing calibration context and project contracts, and are
/// safe to surface in a <c>422</c> problem document. None of them carry a path, host or credential.
/// </remarks>
public static class CalibrationGenerationProblemCodes
{
    // Compatibility tuple.

    /// <summary>The firmware family is not Klipper.</summary>
    public const string FirmwareFamilyUnsupported = "firmware_family_unsupported";

    /// <summary>The G-code dialect is not Klipper.</summary>
    public const string GcodeDialectUnsupported = "gcode_dialect_unsupported";

    /// <summary>The firmware identity was never verified.</summary>
    public const string FirmwareUnverified = "firmware_unverified";

    /// <summary>The authoritative firmware version is absent.</summary>
    public const string FirmwareVersionMissing = "firmware_version_missing";

    /// <summary>The authoritative firmware detection source is absent.</summary>
    public const string FirmwareDetectionSourceMissing = "firmware_detection_source_missing";

    /// <summary>The slicer engine is not OrcaSlicer.</summary>
    public const string SlicerEngineUnsupported = "slicer_engine_unsupported";

    /// <summary>The slicer distribution is not upstream.</summary>
    public const string SlicerDistributionUnsupported = "slicer_distribution_unsupported";

    /// <summary>The slicer version is not the pinned upstream version.</summary>
    public const string SlicerVersionUnsupported = "slicer_version_unsupported";

    /// <summary>The authoritative container digest is absent.</summary>
    public const string SlicerContainerDigestMissing = "slicer_container_digest_missing";

    /// <summary>The authoritative binary digest is absent.</summary>
    public const string SlicerBinaryDigestMissing = "slicer_binary_digest_missing";

    /// <summary>The native profile format is not the pinned upstream format.</summary>
    public const string ProfileFormatUnsupported = "profile_format_unsupported";

    // Context completeness.

    /// <summary>A required authoritative identifier is absent.</summary>
    public const string ContextIdentityMissing = "context_identity_missing";

    /// <summary>The idempotency operation identifier is absent.</summary>
    public const string OperationIdMissing = "operation_id_missing";

    /// <summary>The generator identity is absent.</summary>
    public const string GeneratorIdentityMissing = "generator_identity_missing";

    /// <summary>The authoritative snapshot digest is absent.</summary>
    public const string SnapshotHashMissing = "snapshot_hash_missing";

    /// <summary>The authoritative snapshot capture timestamp is absent.</summary>
    public const string SnapshotTimestampMissing = "snapshot_timestamp_missing";

    /// <summary>The authoritative snapshot digest changed.</summary>
    public const string SnapshotHashMismatch = "snapshot_hash_mismatch";

    /// <summary>The authoritative snapshot is outside its freshness window.</summary>
    public const string SnapshotStale = "snapshot_stale";

    /// <summary>The printer configuration revision advanced past the snapshot.</summary>
    public const string PrinterConfigurationStale = "printer_configuration_stale";

    /// <summary>The exact profile is absent.</summary>
    public const string ProfileMissing = "profile_missing";

    /// <summary>The exact native profile JSON is absent.</summary>
    public const string ProfileJsonMissing = "profile_json_missing";

    /// <summary>The authoritative profile digest is absent.</summary>
    public const string ProfileHashMissing = "profile_hash_missing";

    /// <summary>The profile digest does not match the exact profile JSON.</summary>
    public const string ProfileHashMismatch = "profile_hash_mismatch";

    /// <summary>The toolhead identity is absent.</summary>
    public const string ToolheadMissing = "toolhead_missing";

    /// <summary>The nozzle diameter is absent.</summary>
    public const string NozzleDiameterMissing = "nozzle_diameter_missing";

    /// <summary>The nozzle or hotend temperature ceiling is absent.</summary>
    public const string NozzleLimitMissing = "nozzle_limit_missing";

    /// <summary>The volumetric flow ceiling is absent.</summary>
    public const string VolumetricFlowLimitMissing = "volumetric_flow_limit_missing";

    /// <summary>The build volume is absent.</summary>
    public const string BuildVolumeMissing = "build_volume_missing";

    /// <summary>The build volume cannot hold a calibration footprint.</summary>
    public const string BuildVolumeTooSmall = "build_volume_too_small";

    /// <summary>The printable polygon is malformed.</summary>
    public const string PrintablePolygonInvalid = "printable_polygon_invalid";

    /// <summary>An excluded region is malformed.</summary>
    public const string ExcludedRegionInvalid = "excluded_region_invalid";

    /// <summary>The bed temperature ceiling is absent.</summary>
    public const string BedLimitMissing = "bed_limit_missing";

    /// <summary>The motion limits are absent.</summary>
    public const string MotionLimitMissing = "motion_limit_missing";

    /// <summary>The baseline nozzle temperature is absent.</summary>
    public const string NozzleTemperatureMissing = "nozzle_temperature_missing";

    // Method and sweep.

    /// <summary>The calibration method is not supported.</summary>
    public const string MethodUnsupported = "method_unsupported";

    /// <summary>The method definition version is not supported.</summary>
    public const string MethodDefinitionVersionUnsupported = "method_definition_version_unsupported";

    /// <summary>The requested sweep is malformed.</summary>
    public const string SweepInvalid = "sweep_invalid";

    /// <summary>The sweep resolves to an unsupported number of segments.</summary>
    public const string SegmentCountOutOfRange = "segment_count_out_of_range";

    /// <summary>A requested temperature exceeds the nozzle ceiling.</summary>
    public const string TemperatureAboveNozzleLimit = "temperature_above_nozzle_limit";

    /// <summary>A requested temperature is below the safe extrusion minimum.</summary>
    public const string TemperatureBelowSafeMinimum = "temperature_below_safe_minimum";

    /// <summary>The bed temperature exceeds the bed ceiling.</summary>
    public const string BedTemperatureAboveLimit = "bed_temperature_above_limit";

    /// <summary>A chamber temperature was requested for a printer without a heated chamber.</summary>
    public const string ChamberTemperatureUnsupported = "chamber_temperature_unsupported";

    /// <summary>The chamber temperature exceeds the chamber ceiling.</summary>
    public const string ChamberTemperatureAboveLimit = "chamber_temperature_above_limit";

    /// <summary>A flow ratio is outside the safe range.</summary>
    public const string FlowRatioOutOfRange = "flow_ratio_out_of_range";

    /// <summary>A pressure advance value is outside the safe range.</summary>
    public const string PressureAdvanceOutOfRange = "pressure_advance_out_of_range";

    /// <summary>A retraction length is outside the safe range.</summary>
    public const string RetractionOutOfRange = "retraction_out_of_range";

    /// <summary>A volumetric speed is outside the safe range.</summary>
    public const string VolumetricFlowOutOfRange = "volumetric_flow_out_of_range";

    /// <summary>The resolved layer height is outside the nozzle-derived safe range.</summary>
    public const string LayerHeightOutOfRange = "layer_height_out_of_range";

    /// <summary>The resolved extrusion width is outside the nozzle-derived safe range.</summary>
    public const string LineWidthOutOfRange = "line_width_out_of_range";

    /// <summary>The resolved filament diameter is outside the safe range.</summary>
    public const string FilamentDiameterOutOfRange = "filament_diameter_out_of_range";

    /// <summary>The calibration footprint falls outside the printable polygon.</summary>
    public const string FootprintOutsidePrintablePolygon = "footprint_outside_printable_polygon";

    /// <summary>The calibration footprint overlaps an excluded region.</summary>
    public const string FootprintInsideExcludedRegion = "footprint_inside_excluded_region";

    /// <summary>The specification digest does not match its canonical JSON.</summary>
    public const string SpecificationHashMismatch = "specification_hash_mismatch";

    // Linked asset and model validation.

    /// <summary>The authoritative linked asset is absent.</summary>
    public const string LinkedAssetMissing = "linked_asset_missing";

    /// <summary>The requested asset identity or digest does not match the authoritative asset.</summary>
    public const string LinkedAssetMismatch = "linked_asset_mismatch";

    /// <summary>The model content digest does not match the authoritative record.</summary>
    public const string ModelHashMismatch = "model_hash_mismatch";

    /// <summary>The model format is not supported.</summary>
    public const string ModelFormatUnsupported = "model_format_unsupported";

    /// <summary>The model content is malformed.</summary>
    public const string ModelContentInvalid = "model_content_invalid";

    /// <summary>The model exceeds the accepted size.</summary>
    public const string ModelTooLarge = "model_too_large";

    /// <summary>The model declares an unsupported unit.</summary>
    public const string ModelUnitUnsupported = "model_unit_unsupported";

    /// <summary>The model declares an unsupported transform.</summary>
    public const string ModelTransformUnsupported = "model_transform_unsupported";

    /// <summary>The model exceeds an object, resource or triangle limit.</summary>
    public const string ModelResourceLimitExceeded = "model_resource_limit_exceeded";

    /// <summary>The model archive contains an unsafe entry name.</summary>
    public const string ModelArchivePathTraversal = "model_archive_path_traversal";

    /// <summary>The model archive expands beyond the accepted decompression budget.</summary>
    public const string ModelArchiveDecompressionBomb = "model_archive_decompression_bomb";

    /// <summary>The model archive contains an unsupported resource.</summary>
    public const string ModelArchiveUnsupportedResource = "model_archive_unsupported_resource";

    /// <summary>The model archive XML is malicious or malformed.</summary>
    public const string ModelArchiveXmlUnsafe = "model_archive_xml_unsafe";

    /// <summary>The model does not fit the authoritative build volume.</summary>
    public const string ModelOutsideBuildVolume = "model_outside_build_volume";

    /// <summary>The model does not fit the authoritative printable polygon.</summary>
    public const string ModelOutsidePrintablePolygon = "model_outside_printable_polygon";

    /// <summary>The model overlaps an authoritative excluded region.</summary>
    public const string ModelInsideExcludedRegion = "model_inside_excluded_region";

    /// <summary>The model provenance is absent or untrusted.</summary>
    public const string ModelProvenanceMissing = "model_provenance_missing";

    /// <summary>A method option carried a path, URL, archive, mesh, command or G-code payload.</summary>
    public const string ModelInputNotAllowed = "model_input_not_allowed";

    // Plan compilation.

    /// <summary>A requested native override is not on the allowlist.</summary>
    public const string PlanSettingNotAllowlisted = "plan_setting_not_allowlisted";

    /// <summary>A native profile contains an unsafe arbitrary command field.</summary>
    public const string PlanProfileUnsafeCommand = "plan_profile_unsafe_command";

    /// <summary>A native profile declares an unsupported inheritance chain.</summary>
    public const string PlanProfileInheritanceUnsupported = "plan_profile_inheritance_unsupported";

    /// <summary>The machine profile nozzle does not match the authoritative toolhead nozzle.</summary>
    public const string PlanNozzleMismatch = "plan_nozzle_mismatch";

    /// <summary>The native profile JSON is malformed.</summary>
    public const string PlanProfileJsonInvalid = "plan_profile_json_invalid";

    /// <summary>The pinned slicer identity required by the plan is unavailable.</summary>
    public const string PlanDependencyUnavailable = "plan_dependency_unavailable";

    /// <summary>The plan references a model the specification does not describe.</summary>
    public const string PlanModelMismatch = "plan_model_mismatch";

    // G-code generation, annotation and static validation.

    /// <summary>A command outside the trusted allowlist appeared in emitted G-code.</summary>
    public const string GcodeCommandNotAllowlisted = "gcode_command_not_allowlisted";

    /// <summary>A firmware tuning-tower macro appeared in emitted G-code.</summary>
    public const string GcodeTuningTowerForbidden = "gcode_tuning_tower_forbidden";

    /// <summary>Emitted G-code contains a credential-bearing token.</summary>
    public const string GcodeContainsCredential = "gcode_contains_credential";

    /// <summary>Emitted G-code contains a private or internal URL.</summary>
    public const string GcodeContainsPrivateUrl = "gcode_contains_private_url";

    /// <summary>Emitted G-code contains an absolute filesystem path.</summary>
    public const string GcodeContainsFilesystemPath = "gcode_contains_filesystem_path";

    /// <summary>Emitted G-code contains a shell, host or network command.</summary>
    public const string GcodeContainsHostCommand = "gcode_contains_host_command";

    /// <summary>Emitted G-code moves outside the authoritative build volume.</summary>
    public const string GcodeMotionOutsideBuildVolume = "gcode_motion_outside_build_volume";

    /// <summary>Emitted G-code moves outside the authoritative printable polygon.</summary>
    public const string GcodeMotionOutsidePrintablePolygon = "gcode_motion_outside_printable_polygon";

    /// <summary>Emitted G-code moves into an authoritative excluded region.</summary>
    public const string GcodeMotionInsideExcludedRegion = "gcode_motion_inside_excluded_region";

    /// <summary>Emitted G-code commands a temperature above an authoritative ceiling.</summary>
    public const string GcodeTemperatureAboveLimit = "gcode_temperature_above_limit";

    /// <summary>Emitted G-code commands a feed rate above an authoritative ceiling.</summary>
    public const string GcodeSpeedAboveLimit = "gcode_speed_above_limit";

    /// <summary>Emitted G-code commands an acceleration above an authoritative ceiling.</summary>
    public const string GcodeAccelerationAboveLimit = "gcode_acceleration_above_limit";

    /// <summary>Emitted G-code exceeds the authoritative volumetric flow ceiling.</summary>
    public const string GcodeVolumetricFlowAboveLimit = "gcode_volumetric_flow_above_limit";

    /// <summary>Emitted G-code retracts more than the authoritative ceiling.</summary>
    public const string GcodeRetractionAboveLimit = "gcode_retraction_above_limit";

    /// <summary>Emitted G-code sets a pressure advance outside the safe range.</summary>
    public const string GcodePressureAdvanceOutOfRange = "gcode_pressure_advance_out_of_range";

    /// <summary>Emitted G-code extrudes before a homed, heated, primed state was established.</summary>
    public const string GcodeUnsafeInitialization = "gcode_unsafe_initialization";

    /// <summary>Emitted G-code changes segment without a safe transition.</summary>
    public const string GcodeUnsafeSegmentTransition = "gcode_unsafe_segment_transition";

    /// <summary>Emitted G-code never performs a safe final reset.</summary>
    public const string GcodeMissingFinalReset = "gcode_missing_final_reset";

    /// <summary>Emitted G-code is malformed.</summary>
    public const string GcodeMalformed = "gcode_malformed";

    /// <summary>The emitted G-code digest does not match the manifest.</summary>
    public const string GcodeHashMismatch = "gcode_hash_mismatch";

    /// <summary>The manifest does not describe the emitted G-code.</summary>
    public const string ManifestMismatch = "manifest_mismatch";

    // Profile patch export.

    /// <summary>The selected observation cannot be converted into a typed patch.</summary>
    public const string PatchObservationUnsupported = "patch_observation_unsupported";

    /// <summary>The patch value is outside the safe range.</summary>
    public const string PatchValueOutOfRange = "patch_value_out_of_range";

    /// <summary>The baseline profile required by the patch is absent.</summary>
    public const string PatchBaselineMissing = "patch_baseline_missing";

    /// <summary>The authoritative profile history rejected the patch.</summary>
    public const string PatchPersistenceRejected = "patch_persistence_rejected";

    // Durable generation orchestration.

    /// <summary>An option was supplied that the selected calibration method does not define.</summary>
    public const string OptionNotAllowedForMethod = "option_not_allowed_for_method";

    /// <summary>An option value is outside the accepted shape for its declared type.</summary>
    public const string OptionValueInvalid = "option_value_invalid";

    /// <summary>The request method does not match the immutable attempt method.</summary>
    public const string AttemptMethodMismatch = "attempt_method_mismatch";

    /// <summary>The immutable attempt does not carry a usable stored specification.</summary>
    public const string AttemptSpecificationUnavailable = "attempt_specification_unavailable";

    /// <summary>The stored calibration model could not be resolved through authorized storage.</summary>
    public const string ModelStorageUnavailable = "model_storage_unavailable";

    /// <summary>No registered worker attests the pinned upstream slicer identity.</summary>
    public const string PinnedWorkerUnavailable = "pinned_worker_unavailable";

    /// <summary>The canonical slice submission path is not routable from this process.</summary>
    public const string SliceSubmissionUnavailable = "slice_submission_unavailable";

    /// <summary>The slice job the saga owns reported a durable failure.</summary>
    public const string SliceJobFailed = "slice_job_failed";

    /// <summary>The completed slice job produced no usable G-code artifact.</summary>
    public const string SliceArtifactMissing = "slice_artifact_missing";

    /// <summary>The completed artifact did not match the digest or size it declared.</summary>
    public const string SliceArtifactUnverifiable = "slice_artifact_unverifiable";

    /// <summary>The promotion hop is not currently usable.</summary>
    public const string PromotionUnavailable = "promotion_unavailable";

    /// <summary>The promotion hop refused the verified artifact.</summary>
    public const string PromotionRejected = "promotion_rejected";
}
