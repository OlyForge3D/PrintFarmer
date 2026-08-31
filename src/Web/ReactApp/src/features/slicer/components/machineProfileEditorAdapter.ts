import type { OrcaMachineProfile } from '@/services/slicerProfilesService';
import type {
  MetadataSectionAddition,
  SettingMetadata,
} from '@/features/slicer/components/settings/metadataTypes';

export const BUILD_VOLUME_X_EDITOR_KEY = '__machine_build_volume_x';
export const BUILD_VOLUME_Y_EDITOR_KEY = '__machine_build_volume_y';

type MappedMachineProfileProperty =
  | 'buildVolumeZ'
  | 'nozzleDiameter'
  | 'nozzleType'
  | 'motionType'
  | 'gcodeDialect'
  | 'hasHeatedBed'
  | 'hasHeatedChamber'
  | 'maxBedTemperature'
  | 'maxHotendTemperature'
  | 'retractionLength'
  | 'retractionSpeed'
  | 'detractionSpeed'
  | 'bedType'
  | 'startGcode'
  | 'endGcode'
  | 'maxAccelerationX'
  | 'maxAccelerationY'
  | 'maxFeedrateX'
  | 'maxFeedrateY';

type MachineSettingWireShape =
  | 'scalarString'
  | 'string'
  | 'booleanString'
  | 'singleValueStringArray';

interface MachineProfileSettingMapping {
  profileProperty?: MappedMachineProfileProperty;
  orcaKey: string;
  sourceAliases?: readonly string[];
  wireShape: MachineSettingWireShape;
}

export const MACHINE_PROFILE_SETTING_MAPPINGS: readonly MachineProfileSettingMapping[] = [
  {
    profileProperty: 'buildVolumeZ',
    orcaKey: 'printable_height',
    sourceAliases: ['max_print_height'],
    wireShape: 'scalarString',
  },
  {
    profileProperty: 'nozzleDiameter',
    orcaKey: 'nozzle_diameter',
    wireShape: 'singleValueStringArray',
  },
  {
    profileProperty: 'nozzleType',
    orcaKey: 'nozzle_type',
    wireShape: 'string',
  },
  {
    profileProperty: 'motionType',
    orcaKey: 'printer_type',
    sourceAliases: ['machine_type'],
    wireShape: 'string',
  },
  {
    profileProperty: 'gcodeDialect',
    orcaKey: 'gcode_flavor',
    wireShape: 'string',
  },
  {
    profileProperty: 'hasHeatedBed',
    orcaKey: 'has_heated_bed',
    // bed_custom_texture is only a worker presence heuristic, not an equivalent
    // boolean source; editing this control must not remove or rewrite that texture.
    wireShape: 'booleanString',
  },
  {
    profileProperty: 'hasHeatedChamber',
    orcaKey: 'has_heated_chamber',
    wireShape: 'booleanString',
  },
  {
    profileProperty: 'maxBedTemperature',
    orcaKey: 'max_bed_temp',
    sourceAliases: ['bed_temperature_limit'],
    wireShape: 'scalarString',
  },
  {
    profileProperty: 'maxHotendTemperature',
    orcaKey: 'max_hotend_temp',
    sourceAliases: ['nozzle_temperature_range_high'],
    wireShape: 'scalarString',
  },
  {
    profileProperty: 'retractionLength',
    orcaKey: 'retraction_length',
    sourceAliases: ['retract_length'],
    wireShape: 'singleValueStringArray',
  },
  {
    profileProperty: 'retractionSpeed',
    orcaKey: 'retraction_speed',
    sourceAliases: ['retract_speed'],
    wireShape: 'singleValueStringArray',
  },
  {
    profileProperty: 'detractionSpeed',
    orcaKey: 'deretraction_speed',
    sourceAliases: ['deretract_speed'],
    wireShape: 'singleValueStringArray',
  },
  {
    profileProperty: 'bedType',
    orcaKey: 'curr_bed_type',
    wireShape: 'string',
  },
  {
    profileProperty: 'startGcode',
    orcaKey: 'machine_start_gcode',
    wireShape: 'string',
  },
  {
    profileProperty: 'endGcode',
    orcaKey: 'machine_end_gcode',
    wireShape: 'string',
  },
  {
    profileProperty: 'maxAccelerationX',
    orcaKey: 'machine_max_acceleration_x',
    wireShape: 'singleValueStringArray',
  },
  {
    profileProperty: 'maxAccelerationY',
    orcaKey: 'machine_max_acceleration_y',
    wireShape: 'singleValueStringArray',
  },
  {
    profileProperty: 'maxFeedrateX',
    orcaKey: 'machine_max_speed_x',
    wireShape: 'singleValueStringArray',
  },
  {
    profileProperty: 'maxFeedrateY',
    orcaKey: 'machine_max_speed_y',
    wireShape: 'singleValueStringArray',
  },
  {
    orcaKey: 'use_firmware_retraction',
    wireShape: 'booleanString',
  },
];

export const MACHINE_PROFILE_COMMON_SIMPLE_KEYS = new Set<string>([
  BUILD_VOLUME_X_EDITOR_KEY,
  BUILD_VOLUME_Y_EDITOR_KEY,
  'printable_height',
  'nozzle_diameter',
  'nozzle_type',
  'gcode_flavor',
  'has_heated_bed',
  'has_heated_chamber',
  'max_bed_temp',
  'max_hotend_temp',
  'printer_type',
  'curr_bed_type',
  'use_firmware_retraction',
  'retraction_length',
  'retraction_speed',
  'deretraction_speed',
  'machine_max_speed_x',
  'machine_max_speed_y',
  'machine_max_acceleration_x',
  'machine_max_acceleration_y',
  'machine_start_gcode',
  'machine_end_gcode',
]);

export const MACHINE_PROFILE_ADDITIONAL_SETTINGS: Readonly<Record<string, SettingMetadata>> = {
  [BUILD_VOLUME_X_EDITOR_KEY]: {
    key: BUILD_VOLUME_X_EDITOR_KEY,
    type: 'float',
    coType: 'coFloat',
    label: 'X',
    tooltip: 'Maximum printable width of the bed.',
    unit: 'mm',
    min: 0,
    mode: 'simple',
  },
  [BUILD_VOLUME_Y_EDITOR_KEY]: {
    key: BUILD_VOLUME_Y_EDITOR_KEY,
    type: 'float',
    coType: 'coFloat',
    label: 'Y',
    tooltip: 'Maximum printable depth of the bed.',
    unit: 'mm',
    min: 0,
    mode: 'simple',
  },
  has_heated_bed: {
    key: 'has_heated_bed',
    type: 'bool',
    coType: 'coBool',
    label: 'Heated bed',
    tooltip: 'Whether the printer has a heated build plate.',
    mode: 'simple',
  },
  has_heated_chamber: {
    key: 'has_heated_chamber',
    type: 'bool',
    coType: 'coBool',
    label: 'Heated chamber',
    tooltip: 'Whether the printer has an actively heated chamber.',
    mode: 'simple',
  },
  max_bed_temp: {
    key: 'max_bed_temp',
    type: 'int',
    coType: 'coInt',
    label: 'Maximum bed temperature',
    tooltip: 'Highest safe build-plate temperature supported by the printer.',
    unit: '°C',
    min: 0,
    mode: 'simple',
  },
  max_hotend_temp: {
    key: 'max_hotend_temp',
    type: 'int',
    coType: 'coInt',
    label: 'Maximum hotend temperature',
    tooltip: 'Highest safe nozzle temperature supported by the hotend.',
    unit: '°C',
    min: 0,
    mode: 'simple',
  },
  printer_type: {
    key: 'printer_type',
    type: 'string',
    coType: 'coString',
    label: 'Motion type',
    tooltip: 'Printer motion system, such as CoreXY, Cartesian, or Delta.',
    mode: 'simple',
  },
  curr_bed_type: {
    key: 'curr_bed_type',
    type: 'string',
    coType: 'coString',
    label: 'Default bed type',
    tooltip: 'Default build-plate surface used by this machine profile.',
    mode: 'simple',
  },
};

export const MACHINE_PROFILE_ADDITIONAL_SECTIONS: readonly MetadataSectionAddition[] = [
  {
    tabName: 'Basic information',
    section: {
      name: 'Core hardware',
      icon: 'param_information',
      fields: [
        {
          key: BUILD_VOLUME_X_EDITOR_KEY,
          compound: true,
          compound_label: 'Build volume',
        },
        {
          key: BUILD_VOLUME_Y_EDITOR_KEY,
          compound: true,
          compound_label: 'Build volume',
        },
        { key: 'has_heated_bed', compound: false },
        { key: 'has_heated_chamber', compound: false },
        { key: 'max_bed_temp', compound: false },
        { key: 'max_hotend_temp', compound: false },
        { key: 'printer_type', compound: false },
        { key: 'curr_bed_type', compound: false },
      ],
    },
  },
];

interface PrintableAreaBounds {
  minX: number;
  minY: number;
  width: number;
  depth: number;
  points: readonly { x: number; y: number }[];
}

function hasOwn(settings: Record<string, unknown>, key: string): boolean {
  return Object.prototype.hasOwnProperty.call(settings, key);
}

function formatNumber(value: number): string {
  return Number.isInteger(value) ? String(value) : String(Number(value.toFixed(6)));
}

function toFiniteNumber(value: unknown): number | undefined {
  const number = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(number) ? number : undefined;
}

function parsePrintableArea(value: unknown): PrintableAreaBounds | undefined {
  const rawPoints = Array.isArray(value)
    ? value.map(String)
    : typeof value === 'string'
      ? value.split(',')
      : [];
  const points = rawPoints
    .map((point) => point.trim().match(/^(-?\d+(?:\.\d+)?)x(-?\d+(?:\.\d+)?)$/))
    .filter((match): match is RegExpMatchArray => match !== null)
    .map((match) => ({ x: Number(match[1]), y: Number(match[2]) }));

  if (points.length < 3) return undefined;

  const xs = points.map((point) => point.x);
  const ys = points.map((point) => point.y);
  const minX = Math.min(...xs);
  const maxX = Math.max(...xs);
  const minY = Math.min(...ys);
  const maxY = Math.max(...ys);
  if (![minX, maxX, minY, maxY].every(Number.isFinite)) return undefined;

  return {
    minX,
    minY,
    width: maxX - minX,
    depth: maxY - minY,
    points,
  };
}

function createPrintableArea(
  width: number,
  depth: number,
  sourceArea?: unknown,
): string[] {
  const sourceBounds = parsePrintableArea(sourceArea);
  if (sourceBounds && sourceBounds.width > 0 && sourceBounds.depth > 0) {
    return sourceBounds.points.map((point) => {
      const x = sourceBounds.minX
        + ((point.x - sourceBounds.minX) / sourceBounds.width) * width;
      const y = sourceBounds.minY
        + ((point.y - sourceBounds.minY) / sourceBounds.depth) * depth;
      return `${formatNumber(x)}x${formatNumber(y)}`;
    });
  }

  const minX = sourceBounds?.minX ?? 0;
  const minY = sourceBounds?.minY ?? 0;
  const maxX = minX + width;
  const maxY = minY + depth;

  return [
    `${formatNumber(minX)}x${formatNumber(minY)}`,
    `${formatNumber(maxX)}x${formatNumber(minY)}`,
    `${formatNumber(maxX)}x${formatNumber(maxY)}`,
    `${formatNumber(minX)}x${formatNumber(maxY)}`,
  ];
}

function toWireValue(
  value: unknown,
  wireShape: MachineSettingWireShape,
): unknown {
  if (value === undefined || value === null) return undefined;

  switch (wireShape) {
    case 'booleanString':
      if (typeof value !== 'boolean') return undefined;
      return value ? '1' : '0';
    case 'singleValueStringArray': {
      // Promoted DTOs expose only the worker's first parsed extruder value, so
      // hydration can restore exactly one Orca array entry without inventing others.
      const number = toFiniteNumber(value);
      return number === undefined ? undefined : [formatNumber(number)];
    }
    case 'scalarString': {
      const number = toFiniteNumber(value);
      return number === undefined ? undefined : formatNumber(number);
    }
    case 'string':
      return typeof value === 'string' ? value : undefined;
    default:
      return undefined;
  }
}

function normalizeEditedValue(
  value: unknown,
  wireShape: MachineSettingWireShape,
): unknown {
  switch (wireShape) {
    case 'booleanString':
      if (value === true || value === 'true' || value === '1') return '1';
      if (value === false || value === 'false' || value === '0') return '0';
      throw new Error('Boolean machine settings must be true or false.');
    case 'singleValueStringArray': {
      const values = Array.isArray(value) ? value : String(value).split(',');
      const normalized = values.map((item) => String(item).trim()).filter(Boolean);
      if (
        normalized.length === 0
        || normalized.some((item) => !Number.isFinite(Number(item)))
      ) {
        throw new Error('Per-extruder machine settings require at least one numeric value.');
      }
      return normalized.map((item) => formatNumber(Number(item)));
    }
    case 'scalarString': {
      const number = toFiniteNumber(value);
      return number === undefined ? value : formatNumber(number);
    }
    case 'string':
      return String(value);
    default:
      return value;
  }
}

function valuesEqual(left: unknown, right: unknown): boolean {
  return JSON.stringify(left) === JSON.stringify(right);
}

export function areProfileSettingsEqual(
  settings: Record<string, unknown>,
  originalSettings: Record<string, unknown>,
): boolean {
  const settingsKeys = Object.keys(settings);
  const originalKeys = Object.keys(originalSettings);
  return settingsKeys.length === originalKeys.length
    && settingsKeys.every((key) =>
      hasOwn(originalSettings, key) && valuesEqual(settings[key], originalSettings[key]));
}

export function hydrateMachineProfileSettings(
  profile: OrcaMachineProfile,
): Record<string, unknown> {
  const settings = { ...(profile.settings ?? {}) };

  for (const mapping of MACHINE_PROFILE_SETTING_MAPPINGS) {
    if (hasOwn(settings, mapping.orcaKey) || !mapping.profileProperty) {
      continue;
    }

    const hydrated = toWireValue(profile[mapping.profileProperty], mapping.wireShape);
    if (hydrated !== undefined) {
      settings[mapping.orcaKey] = hydrated;
    }
  }

  if (!hasOwn(settings, 'printable_area')) {
    const promotedArea = typeof profile.printableArea === 'string'
      && parsePrintableArea(profile.printableArea)
      ? profile.printableArea
      : undefined;
    const width = toFiniteNumber(profile.buildVolumeX);
    const depth = toFiniteNumber(profile.buildVolumeY);

    if (promotedArea) {
      settings.printable_area = promotedArea
        .split(',')
        .map((point) => point.trim());
    } else if (width !== undefined && depth !== undefined) {
      settings.printable_area = createPrintableArea(width, depth);
    }
  }

  const bounds = parsePrintableArea(settings.printable_area);
  const width = bounds?.width ?? toFiniteNumber(profile.buildVolumeX);
  const depth = bounds?.depth ?? toFiniteNumber(profile.buildVolumeY);
  if (width !== undefined) settings[BUILD_VOLUME_X_EDITOR_KEY] = width;
  if (depth !== undefined) settings[BUILD_VOLUME_Y_EDITOR_KEY] = depth;

  return settings;
}

export function serializeMachineProfileSettings(
  settings: Record<string, unknown>,
  originalSettings: Record<string, unknown>,
  rawSettings: Record<string, unknown>,
): Record<string, unknown> {
  const serialized = { ...rawSettings };
  const editorKeys = new Set(
    MACHINE_PROFILE_SETTING_MAPPINGS.map((mapping) => mapping.orcaKey),
  );

  for (const key of new Set([...Object.keys(settings), ...Object.keys(originalSettings)])) {
    if (
      editorKeys.has(key)
      || key === BUILD_VOLUME_X_EDITOR_KEY
      || key === BUILD_VOLUME_Y_EDITOR_KEY
      || (
        valuesEqual(settings[key], originalSettings[key])
        && hasOwn(settings, key) === hasOwn(originalSettings, key)
      )
    ) {
      continue;
    }

    if (hasOwn(settings, key)) {
      serialized[key] = settings[key];
    } else {
      delete serialized[key];
    }
  }

  const widthChanged = !valuesEqual(
    settings[BUILD_VOLUME_X_EDITOR_KEY],
    originalSettings[BUILD_VOLUME_X_EDITOR_KEY],
  );
  const depthChanged = !valuesEqual(
    settings[BUILD_VOLUME_Y_EDITOR_KEY],
    originalSettings[BUILD_VOLUME_Y_EDITOR_KEY],
  );

  if (widthChanged || depthChanged) {
    const width = toFiniteNumber(settings[BUILD_VOLUME_X_EDITOR_KEY]);
    const depth = toFiniteNumber(settings[BUILD_VOLUME_Y_EDITOR_KEY]);
    if (width === undefined || depth === undefined || width <= 0 || depth <= 0) {
      throw new Error(
        'Build volume requires positive X and Y dimensions; enter values greater than zero for both.',
      );
    }
    serialized.printable_area = createPrintableArea(
      width,
      depth,
      originalSettings.printable_area,
    );
  }

  delete serialized[BUILD_VOLUME_X_EDITOR_KEY];
  delete serialized[BUILD_VOLUME_Y_EDITOR_KEY];

  for (const mapping of MACHINE_PROFILE_SETTING_MAPPINGS) {
    if (
      valuesEqual(settings[mapping.orcaKey], originalSettings[mapping.orcaKey])
      && hasOwn(settings, mapping.orcaKey) === hasOwn(originalSettings, mapping.orcaKey)
    ) {
      continue;
    }

    for (const alias of mapping.sourceAliases ?? []) {
      delete serialized[alias];
    }

    if (hasOwn(settings, mapping.orcaKey)) {
      serialized[mapping.orcaKey] = normalizeEditedValue(
        settings[mapping.orcaKey],
        mapping.wireShape,
      );
    } else {
      delete serialized[mapping.orcaKey];
    }
  }

  return serialized;
}
