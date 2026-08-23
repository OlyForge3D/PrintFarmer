/**
 * Human-readable guidance for calibration-eligibility field paths (issue #1924).
 *
 * `CalibrationContextDto.missingInputs` is a flat list of raw JSON-pointer-ish paths
 * (e.g. `toolheads[0].extruderGearRatio`, `buildVolume.x`, `firmware.verified`) that the
 * backend eligibility gate emits. Surfacing those paths directly to an operator is not
 * actionable: it doesn't say what the field means, who can resolve it, or where. This
 * module classifies each path into a human label plus a resolution group so the modal
 * can group remaining work by "who fixes this and where" instead of dumping raw paths.
 *
 * This table is intentionally over-inclusive relative to what any single printer will
 * ever report, and any path not recognized here still degrades gracefully (humanized
 * label, `other` group) rather than crashing or falling back to raw text — issue #1922
 * will shrink the actual missingInputs list over time as more fields resolve from the
 * catalog, and this table does not need to track that change 1:1.
 */

/** Who can resolve a missing field, and therefore where it belongs in the modal. */
export type CalibrationFieldGroup = 'here' | 'profile' | 'signoff' | 'admin' | 'other';

export interface CalibrationFieldGroupInfo {
  title: string;
  description: string;
}

/** Display order and copy for each resolution group. */
export const CALIBRATION_FIELD_GROUPS: Record<CalibrationFieldGroup, CalibrationFieldGroupInfo> = {
  here: {
    title: 'You can set this here',
    description: 'Fill these in below, in this dialog.',
  },
  profile: {
    title: 'From your selected profiles',
    description:
      'Resolved automatically once compatible machine, process, and filament profiles are bound below.',
  },
  signoff: {
    title: 'Needs your sign-off',
    description: 'Confirm these facts against the physical hardware using the buttons below.',
  },
  admin: {
    title: 'Needs an administrator',
    description:
      "Not editable in this dialog. An administrator can set these from the printer's admin " +
      'settings (Printers → Edit printer), which saves via PUT /api/printers/{id}.',
  },
  other: {
    title: 'Other setup needed',
    description: 'See technical details below for the raw field.',
  },
};

export interface CalibrationFieldGuidance {
  label: string;
  group: CalibrationFieldGroup;
}

/** Guidance for fields shared by every toolhead, keyed by the path suffix after `toolheads[N].`. */
const TOOLHEAD_FIELD_GUIDANCE: Record<string, CalibrationFieldGuidance> = {
  'offset.x': { label: 'Offset X (mm)', group: 'here' },
  'offset.y': { label: 'Offset Y (mm)', group: 'here' },
  'offset.z': { label: 'Offset Z (mm)', group: 'here' },
  nozzleDiameter: { label: 'Nozzle diameter', group: 'profile' },
  nozzleType: { label: 'Nozzle type', group: 'profile' },
  nozzleMaterial: { label: 'Nozzle material', group: 'here' },
  nozzleMaxTemperature: { label: 'Nozzle max temperature', group: 'profile' },
  nozzleIsHardened: { label: 'Nozzle is hardened', group: 'here' },
  hotendMaxTemperature: { label: 'Hotend max temperature', group: 'profile' },
  maxVolumetricFlow: { label: 'Max volumetric flow', group: 'here' },
  driveType: { label: 'Drive type', group: 'here' },
  isDirectDrive: { label: 'Is direct drive', group: 'here' },
  extruderGearRatio: { label: 'Extruder gear ratio', group: 'here' },
  supportedMaterials: { label: 'Supported materials', group: 'admin' },
};

/** Guidance for every other (non-toolhead) field path, keyed by its exact path. */
const FIELD_GUIDANCE: Record<string, CalibrationFieldGuidance> = {
  activeToolheadIndex: { label: 'Active toolhead', group: 'here' },
  supportsMultiExtruderStatus: { label: 'Multi-extruder status support', group: 'admin' },
  toolheads: { label: 'At least one physical toolhead', group: 'admin' },

  'buildVolume.x': { label: 'Build volume X (mm)', group: 'profile' },
  'buildVolume.y': { label: 'Build volume Y (mm)', group: 'profile' },
  'buildVolume.z': { label: 'Build volume Z (mm)', group: 'profile' },
  'bedOrigin.x': { label: 'Bed origin X (mm)', group: 'profile' },
  'bedOrigin.y': { label: 'Bed origin Y (mm)', group: 'profile' },
  printablePolygon: { label: 'Printable bed area', group: 'profile' },
  motionType: { label: 'Motion system type', group: 'profile' },

  maxPrintSpeed: { label: 'Max print speed', group: 'admin' },
  maxTravelSpeed: { label: 'Max travel speed', group: 'profile' },
  maxAcceleration: { label: 'Max acceleration', group: 'profile' },
  maxTravelAcceleration: { label: 'Max travel acceleration', group: 'admin' },

  hasHeatedBed: { label: 'Has heated bed', group: 'profile' },
  maxBedTemperature: { label: 'Max bed temperature', group: 'admin' },
  hasEnclosure: { label: 'Has enclosure', group: 'admin' },
  hasHeatedChamber: { label: 'Has heated chamber', group: 'profile' },
  maxChamberTemperature: { label: 'Max chamber temperature', group: 'admin' },

  supportsPressureAdvance: { label: 'Supports pressure advance', group: 'here' },
  supportsFirmwareRetraction: { label: 'Supports firmware retraction', group: 'here' },

  'firmware.family': { label: 'Firmware family', group: 'here' },
  'firmware.gcodeDialect': { label: 'G-code dialect', group: 'here' },
  'firmware.detectionSource': { label: 'Firmware detection source', group: 'here' },
  'firmware.version': { label: 'Firmware version', group: 'here' },
  'firmware.detectionVersion': { label: 'Firmware detector/configuration version', group: 'here' },
  'firmware.detectionConfidence': { label: 'Firmware detection confidence', group: 'here' },
  'firmware.verified': { label: 'Firmware identity sign-off', group: 'signoff' },
  'firmware.detectedAtUtc': { label: 'Firmware detection freshness', group: 'here' },

  'slicer.engine': { label: 'Slicer engine', group: 'profile' },
  'slicer.distribution': { label: 'Slicer distribution', group: 'profile' },
  'slicer.version': { label: 'Slicer version', group: 'profile' },
  'slicer.profileFormat': { label: 'Slicer profile format', group: 'profile' },
  'slicer.machineProfileId': { label: 'Machine profile binding', group: 'here' },
  'slicer.processProfileId': { label: 'Process profile binding', group: 'here' },
  'slicer.filamentProfileId': { label: 'Filament profile binding', group: 'here' },

  'profiles.machine': { label: 'Machine profile binding', group: 'here' },
  'profiles.process': { label: 'Process profile binding', group: 'here' },
  'profiles.filament': { label: 'Filament profile binding', group: 'here' },
  'profiles.process.compatiblePrinters': { label: 'Process profile compatibility', group: 'profile' },
  'profiles.filament.compatiblePrinters': { label: 'Filament profile compatibility', group: 'profile' },
  'profiles.machine.exactJson.gcode_flavor': { label: 'Machine profile G-code flavor', group: 'profile' },
  'profiles.machine.exactJson.nozzle_diameter': { label: 'Machine profile nozzle diameter', group: 'profile' },
  'profiles.filament.material': { label: 'Filament material', group: 'profile' },
  'profiles.filament.nozzleTemperature': { label: 'Filament nozzle temperature', group: 'profile' },
  'profiles.filament.bedTemperature': { label: 'Filament bed temperature', group: 'profile' },
  'profiles.filament.exactJson.required_nozzle_HRC': {
    label: 'Filament required nozzle hardness',
    group: 'profile',
  },

  calibrationHardwareVerifiedAtUtc: { label: 'Hardware verification sign-off', group: 'signoff' },
};

const TOOLHEAD_PATH_PATTERN = /^toolheads\[(\d+)]\.(.+)$/;

/** Converts a camelCase/dotted path segment into a readable fallback label. */
function humanizeFallbackLabel(path: string): string {
  const lastSegment = path.split('.').pop() ?? path;
  const spaced = lastSegment
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .trim();
  return spaced.length > 0 ? spaced.charAt(0).toUpperCase() + spaced.slice(1) : path;
}

export interface ClassifiedCalibrationField {
  /** The raw path as reported by the backend, kept for the technical-details disclosure. */
  path: string;
  /** Human-readable label for primary UI text. */
  label: string;
  group: CalibrationFieldGroup;
}

/** Classifies a single raw `missingInputs` path into a human label and resolution group. */
export function classifyCalibrationField(path: string): ClassifiedCalibrationField {
  const toolheadMatch = TOOLHEAD_PATH_PATTERN.exec(path);
  if (toolheadMatch) {
    const [, index, suffix] = toolheadMatch;
    const guidance = TOOLHEAD_FIELD_GUIDANCE[suffix];
    if (guidance) {
      return { path, label: `Toolhead ${index} — ${guidance.label}`, group: guidance.group };
    }
    return { path, label: `Toolhead ${index} — ${humanizeFallbackLabel(suffix)}`, group: 'other' };
  }

  const guidance = FIELD_GUIDANCE[path];
  if (guidance) {
    return { path, label: guidance.label, group: guidance.group };
  }
  return { path, label: humanizeFallbackLabel(path), group: 'other' };
}

export type GroupedCalibrationFields = Record<CalibrationFieldGroup, ClassifiedCalibrationField[]>;

/**
 * Classifies and groups every missing input, suppressing profile-derivable fields until
 * profiles are actually bound (AC: "do not surface profile-derivable fields as blockers
 * before profiles are selected"). Once every profile is bound, profile-derived fields
 * that are still missing become real blockers again (e.g. an incompatible profile) and
 * are shown normally.
 */
export function groupMissingInputs(
  missingInputs: readonly string[],
  allProfilesBound: boolean
): GroupedCalibrationFields {
  const grouped: GroupedCalibrationFields = { here: [], profile: [], signoff: [], admin: [], other: [] };
  for (const path of missingInputs) {
    const classified = classifyCalibrationField(path);
    if (classified.group === 'profile' && !allProfilesBound) {
      continue;
    }
    grouped[classified.group].push(classified);
  }
  return grouped;
}

export type CalibrationStatus = 'ready' | 'in-progress' | 'needs-setup';

export interface CalibrationProgressSignals {
  eligible: boolean;
  /** True once machine, process, and filament profiles are all bound. */
  anyProfileBound: boolean;
  /** True once any firmware fact has been detected (family, version, etc.). */
  firmwareDetected: boolean;
  /** True once an operator has attested the hardware or firmware identity. */
  hardwareOrFirmwareVerified: boolean;
}

/**
 * Determines the plain-language onboarding status for the modal's headline banner.
 * An uncalibrated printer should read as "not set up yet", not as an error state.
 *
 * Deliberately signal-based rather than counting `missingInputs` — that count shrinks
 * over time as issue #1922 resolves more fields from the catalog, so a threshold tied to
 * today's field count would silently misclassify printers once that list changes shape.
 */
export function getCalibrationStatus(signals: CalibrationProgressSignals): CalibrationStatus {
  if (signals.eligible) {
    return 'ready';
  }
  const hasStartedSetup =
    signals.anyProfileBound || signals.firmwareDetected || signals.hardwareOrFirmwareVerified;
  return hasStartedSetup ? 'in-progress' : 'needs-setup';
}
