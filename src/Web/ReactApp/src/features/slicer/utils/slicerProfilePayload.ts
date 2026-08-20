/**
 * Builds the `slicerProfileJson` payload for a slice submission.
 *
 * Extracted from NewSliceJobPage so the serialization contract can be pinned by
 * a unit test without standing up the page's submit preconditions (engine
 * registry, process preset, model selection).
 *
 * The contract that matters here: `machineProfileName` MUST be the canonical
 * profile name the slicer API matches on — never a display label. The picker
 * trims the nozzle token for readability ("Prusa CORE One HF 0.4 nozzle" reads
 * as "Prusa CORE One HF"), and sending that trimmed form would fail to resolve
 * a profile on the worker.
 */

export interface SlicerProfilePayloadInput {
  /** Canonical machine profile name. Never a trimmed display label. */
  machineProfileName: string;
  /** Canonical filament profile name for single-toolhead printers. */
  filamentProfileName: string;
  /** Per-extruder filament profile names; omitted for single-toolhead printers. */
  filamentProfileNames?: string[];
  /** Per-extruder filament colours ("#RRGGBB"); omitted for single-toolhead printers. */
  filamentColours?: string[];
  /** Single-toolhead filament colour ("#RRGGBB"). */
  filamentColour?: string;
  /**
   * Process preset id as held in page state — may carry a `system:` or
   * `custom:` prefix, which is stripped for the wire payload.
   */
  processPresetId: string;
  /** Process setting overrides, already scrubbed for the target engine version. */
  overrides: Record<string, unknown>;
}

/**
 * Strips the source prefix from a process preset id.
 *
 * Page state distinguishes system and custom presets by prefix; the worker
 * expects the bare profile name.
 */
export function stripProcessPresetPrefix(processPresetId: string): string {
  if (processPresetId.startsWith('system:')) {
    return processPresetId.slice('system:'.length);
  }
  if (processPresetId.startsWith('custom:')) {
    return processPresetId.slice('custom:'.length);
  }
  return processPresetId;
}

/**
 * Serializes the slicer profile selection for `SubmitSliceJobRequest`.
 *
 * @param input Selections in their canonical (untrimmed) form.
 * @returns The JSON string sent as `slicerProfileJson`.
 */
export function buildSlicerProfileJson(input: SlicerProfilePayloadInput): string {
  return JSON.stringify({
    machineProfileName: input.machineProfileName,
    filamentProfileName: input.filamentProfileName,
    // Per-extruder names let workers resolve multi-material assignments.
    ...(input.filamentProfileNames ? { filamentProfileNames: input.filamentProfileNames } : {}),
    // Per-slice colour overrides are preview / G-code metadata only.
    ...(input.filamentColours ? { filamentColours: input.filamentColours } : {}),
    ...(input.filamentColour ? { filamentColour: input.filamentColour } : {}),
    processProfileName: stripProcessPresetPrefix(input.processPresetId),
    overrides: input.overrides,
  });
}
