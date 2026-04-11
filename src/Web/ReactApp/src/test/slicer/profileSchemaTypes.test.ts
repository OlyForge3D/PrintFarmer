import { describe, it, expect } from 'vitest';
import {
  DEFAULT_ORCA_FILAMENT_SETTINGS,
  ORCA_FILAMENT_CATEGORY_MAP,
  MATERIAL_PRESETS,
} from '@/features/slicer/components/settings/filamentSettingsTypes';
import type { FilamentCategory } from '@/features/slicer/components/settings/filamentSettingsTypes';
import {
  DEFAULT_ORCA_PROCESS_SETTINGS,
  ORCA_PROCESS_CATEGORY_MAP,
  INFILL_PATTERN_INFO,
  BED_ADHESION_INFO,
  isValidCategory,
} from '@/features/slicer/components/settings/slicerSettingsTypes';
import type { SettingsCategory } from '@/features/slicer/components/settings/slicerSettingsTypes';
import {
  DEFAULT_ORCA_MACHINE_SETTINGS,
  ORCA_MACHINE_CATEGORY_MAP,
  GCODE_DIALECT_LABELS,
  MOTION_TYPE_LABELS,
  NOZZLE_TYPE_LABELS,
  BED_TYPE_LABELS,
  PROBE_TYPE_LABELS,
  PRINTER_PRESETS,
} from '@/features/slicer/components/settings/machineSettingsTypes';
import type { MachineCategory } from '@/features/slicer/components/settings/machineSettingsTypes';
import type { ProfileFieldMetadata, EnumOption, ProfileTypeSchema } from '@/types/api';

// ---------------------------------------------------------------------------
// FilamentSettingsTypes contract tests
// ---------------------------------------------------------------------------
describe('FilamentSettingsTypes', () => {
  describe('DEFAULT_ORCA_FILAMENT_SETTINGS', () => {
    it('contains all expected keys', () => {
      const expected = ['nozzle_temperature', 'hot_plate_temp', 'filament_retraction_length'];
      for (const key of expected) {
        expect(DEFAULT_ORCA_FILAMENT_SETTINGS).toHaveProperty(key);
      }
    });

    it('has numeric temperature values', () => {
      expect(typeof DEFAULT_ORCA_FILAMENT_SETTINGS.nozzle_temperature).toBe('number');
      expect(typeof DEFAULT_ORCA_FILAMENT_SETTINGS.hot_plate_temp).toBe('number');
    });
  });

  describe('ORCA_FILAMENT_CATEGORY_MAP', () => {
    const validCategories: FilamentCategory[] = [
      'filament', 'cooling', 'setting_overrides', 'advanced', 'multimaterial', 'dependencies', 'notes',
    ];

    it('covers expected categories', () => {
      const usedCategories = new Set(Object.values(ORCA_FILAMENT_CATEGORY_MAP));
      for (const cat of ['filament', 'cooling', 'setting_overrides']) {
        expect(usedCategories.has(cat as FilamentCategory)).toBe(true);
      }
    });

    it('maps every field to a valid category', () => {
      for (const [, category] of Object.entries(ORCA_FILAMENT_CATEGORY_MAP)) {
        expect(validCategories).toContain(category);
      }
    });

    it('maps temperature fields to filament category', () => {
      expect(ORCA_FILAMENT_CATEGORY_MAP['nozzle_temperature']).toBe('filament');
      expect(ORCA_FILAMENT_CATEGORY_MAP['hot_plate_temp']).toBe('filament');
    });

    it('maps retraction fields to setting_overrides category', () => {
      expect(ORCA_FILAMENT_CATEGORY_MAP['filament_retraction_length']).toBe('setting_overrides');
    });
  });

  describe('MATERIAL_PRESETS', () => {
    it('includes common materials', () => {
      expect(MATERIAL_PRESETS).toHaveProperty('PLA');
      expect(MATERIAL_PRESETS).toHaveProperty('PETG');
      expect(MATERIAL_PRESETS).toHaveProperty('ABS');
      expect(MATERIAL_PRESETS).toHaveProperty('TPU');
    });

    it('each preset has temperature settings', () => {
      for (const [, preset] of Object.entries(MATERIAL_PRESETS)) {
        expect(preset.nozzle_temperature).toBeGreaterThan(0);
        expect(preset.hot_plate_temp).toBeGreaterThanOrEqual(0);
      }
    });
  });
});

// ---------------------------------------------------------------------------
// SlicerSettingsTypes contract tests
// ---------------------------------------------------------------------------
describe('SlicerSettingsTypes', () => {
  describe('DEFAULT_ORCA_PROCESS_SETTINGS', () => {
    it('contains core keys', () => {
      expect(DEFAULT_ORCA_PROCESS_SETTINGS).toHaveProperty('sparse_infill_density');
      expect(DEFAULT_ORCA_PROCESS_SETTINGS).toHaveProperty('sparse_infill_pattern');
      expect(DEFAULT_ORCA_PROCESS_SETTINGS).toHaveProperty('wall_loops');
      expect(DEFAULT_ORCA_PROCESS_SETTINGS).toHaveProperty('brim_type');
      expect(DEFAULT_ORCA_PROCESS_SETTINGS).toHaveProperty('enable_support');
    });

    it('has reasonable default types', () => {
      expect(typeof DEFAULT_ORCA_PROCESS_SETTINGS.sparse_infill_density).toBe('number');
      expect(typeof DEFAULT_ORCA_PROCESS_SETTINGS.wall_loops).toBe('number');
      expect(typeof DEFAULT_ORCA_PROCESS_SETTINGS.enable_support).toBe('boolean');
      expect(typeof DEFAULT_ORCA_PROCESS_SETTINGS.sparse_infill_pattern).toBe('string');
    });

    it('has layer and line controls', () => {
      expect(DEFAULT_ORCA_PROCESS_SETTINGS).toHaveProperty('layer_height');
      expect(DEFAULT_ORCA_PROCESS_SETTINGS).toHaveProperty('initial_layer_print_height');
      expect(DEFAULT_ORCA_PROCESS_SETTINGS).toHaveProperty('line_width');
      expect(DEFAULT_ORCA_PROCESS_SETTINGS).toHaveProperty('top_shell_layers');
      expect(DEFAULT_ORCA_PROCESS_SETTINGS).toHaveProperty('bottom_shell_layers');
    });

    it('has speed settings as numbers', () => {
      expect(typeof DEFAULT_ORCA_PROCESS_SETTINGS.outer_wall_speed).toBe('number');
      expect(typeof DEFAULT_ORCA_PROCESS_SETTINGS.travel_speed).toBe('number');
    });

    it('has support settings', () => {
      expect(DEFAULT_ORCA_PROCESS_SETTINGS).toHaveProperty('support_type');
      expect(DEFAULT_ORCA_PROCESS_SETTINGS).toHaveProperty('support_threshold_angle');
    });
  });

  describe('ORCA_PROCESS_CATEGORY_MAP', () => {
    const validCategories: SettingsCategory[] = [
      'quality', 'strength', 'speed', 'support', 'multimaterial', 'others',
    ];

    it('maps key process settings', () => {
      expect(ORCA_PROCESS_CATEGORY_MAP['layer_height']).toBe('quality');
      expect(ORCA_PROCESS_CATEGORY_MAP['sparse_infill_density']).toBe('strength');
      expect(ORCA_PROCESS_CATEGORY_MAP['outer_wall_speed']).toBe('speed');
      expect(ORCA_PROCESS_CATEGORY_MAP['enable_support']).toBe('support');
    });

    it('maps every field to a valid category', () => {
      for (const [, category] of Object.entries(ORCA_PROCESS_CATEGORY_MAP)) {
        expect(validCategories).toContain(category);
      }
    });

    it('covers all category values', () => {
      const usedCategories = new Set(Object.values(ORCA_PROCESS_CATEGORY_MAP));
      for (const cat of ['quality', 'strength', 'speed', 'support']) {
        expect(usedCategories.has(cat as SettingsCategory)).toBe(true);
      }
    });
  });

  describe('INFILL_PATTERN_INFO', () => {
    it('has label and description for each pattern', () => {
      for (const [, info] of Object.entries(INFILL_PATTERN_INFO)) {
        expect(typeof info.label).toBe('string');
        expect(typeof info.description).toBe('string');
        expect(info.label.length).toBeGreaterThan(0);
      }
    });
  });

  describe('BED_ADHESION_INFO', () => {
    it('has label and description for each adhesion type', () => {
      for (const [, info] of Object.entries(BED_ADHESION_INFO)) {
        expect(typeof info.label).toBe('string');
        expect(typeof info.description).toBe('string');
      }
    });
  });

  describe('isValidCategory', () => {
    it('returns true for valid categories', () => {
      expect(isValidCategory('quality')).toBe(true);
      expect(isValidCategory('speed')).toBe(true);
      expect(isValidCategory('support')).toBe(true);
    });

    it('returns false for invalid categories', () => {
      expect(isValidCategory('nonexistent')).toBe(false);
      expect(isValidCategory('')).toBe(false);
    });
  });
});

// ---------------------------------------------------------------------------
// MachineSettingsTypes contract tests
// ---------------------------------------------------------------------------
describe('MachineSettingsTypes', () => {
  describe('DEFAULT_ORCA_MACHINE_SETTINGS', () => {
    it('contains key machine fields', () => {
      expect(DEFAULT_ORCA_MACHINE_SETTINGS).toHaveProperty('bed_size_x');
      expect(DEFAULT_ORCA_MACHINE_SETTINGS).toHaveProperty('bed_size_y');
      expect(DEFAULT_ORCA_MACHINE_SETTINGS).toHaveProperty('printable_height');
      expect(DEFAULT_ORCA_MACHINE_SETTINGS).toHaveProperty('nozzle_diameter');
      expect(DEFAULT_ORCA_MACHINE_SETTINGS).toHaveProperty('max_print_speed');
    });

    it('has positive build volume values', () => {
      expect(DEFAULT_ORCA_MACHINE_SETTINGS.bed_size_x).toBeGreaterThan(0);
      expect(DEFAULT_ORCA_MACHINE_SETTINGS.bed_size_y).toBeGreaterThan(0);
      expect(DEFAULT_ORCA_MACHINE_SETTINGS.printable_height).toBeGreaterThan(0);
    });

    it('has motion and capability fields', () => {
      expect(DEFAULT_ORCA_MACHINE_SETTINGS).toHaveProperty('motion_type');
      expect(DEFAULT_ORCA_MACHINE_SETTINGS).toHaveProperty('machine_max_acceleration_x');
      expect(DEFAULT_ORCA_MACHINE_SETTINGS).toHaveProperty('gcode_flavor');
      expect(DEFAULT_ORCA_MACHINE_SETTINGS).toHaveProperty('has_heated_bed');
    });

    it('has gcode fields as strings', () => {
      expect(typeof DEFAULT_ORCA_MACHINE_SETTINGS.machine_start_gcode).toBe('string');
      expect(typeof DEFAULT_ORCA_MACHINE_SETTINGS.machine_end_gcode).toBe('string');
    });
  });

  describe('ORCA_MACHINE_CATEGORY_MAP', () => {
    const validCategories: MachineCategory[] = [
      'basic_information', 'machine_gcode', 'multimaterial', 'extruder', 'motion_ability', 'notes',
    ];

    it('covers all expected categories', () => {
      const usedCategories = new Set(Object.values(ORCA_MACHINE_CATEGORY_MAP));
      for (const cat of ['basic_information', 'extruder', 'motion_ability', 'machine_gcode']) {
        expect(usedCategories.has(cat as MachineCategory)).toBe(true);
      }
    });

    it('maps every field to a valid category', () => {
      for (const [, category] of Object.entries(ORCA_MACHINE_CATEGORY_MAP)) {
        expect(validCategories).toContain(category);
      }
    });

    it('maps key fields to correct categories', () => {
      expect(ORCA_MACHINE_CATEGORY_MAP['nozzle_diameter']).toBe('extruder');
      expect(ORCA_MACHINE_CATEGORY_MAP['bed_type']).toBe('basic_information');
      expect(ORCA_MACHINE_CATEGORY_MAP['has_heated_bed']).toBe('basic_information');
      expect(ORCA_MACHINE_CATEGORY_MAP['machine_start_gcode']).toBe('machine_gcode');
      expect(ORCA_MACHINE_CATEGORY_MAP['motion_type']).toBe('motion_ability');
    });
  });

  describe('Label maps', () => {
    it('GCODE_DIALECT_LABELS covers common dialects', () => {
      expect(GCODE_DIALECT_LABELS).toHaveProperty('marlin');
      expect(GCODE_DIALECT_LABELS).toHaveProperty('klipper');
      expect(GCODE_DIALECT_LABELS).toHaveProperty('reprap');
    });

    it('MOTION_TYPE_LABELS covers common motion types', () => {
      expect(MOTION_TYPE_LABELS).toHaveProperty('cartesian');
      expect(MOTION_TYPE_LABELS).toHaveProperty('corexy');
      expect(MOTION_TYPE_LABELS).toHaveProperty('delta');
    });

    it('NOZZLE_TYPE_LABELS has expected entries', () => {
      expect(NOZZLE_TYPE_LABELS).toHaveProperty('brass');
      expect(NOZZLE_TYPE_LABELS).toHaveProperty('hardened_steel');
    });

    it('BED_TYPE_LABELS has expected entries', () => {
      expect(BED_TYPE_LABELS).toHaveProperty('textured_pei');
      expect(BED_TYPE_LABELS).toHaveProperty('glass');
    });

    it('PROBE_TYPE_LABELS has expected entries', () => {
      expect(PROBE_TYPE_LABELS).toHaveProperty('bltouch');
      expect(PROBE_TYPE_LABELS).toHaveProperty('none');
    });
  });

  describe('PRINTER_PRESETS', () => {
    it('includes common printer models', () => {
      expect(PRINTER_PRESETS).toHaveProperty('Prusa MK4');
      expect(PRINTER_PRESETS).toHaveProperty('Voron 2.4');
      expect(PRINTER_PRESETS).toHaveProperty('Bambu Lab X1C');
    });

    it('each preset has build volume', () => {
      for (const [, preset] of Object.entries(PRINTER_PRESETS)) {
        expect(preset.bed_size_x).toBeGreaterThan(0);
        expect(preset.bed_size_y).toBeGreaterThan(0);
        expect(preset.printable_height).toBeGreaterThan(0);
      }
    });
  });
});

// ---------------------------------------------------------------------------
// ProfileFieldMetadata type guard / shape tests
// ---------------------------------------------------------------------------
describe('ProfileFieldMetadata shape', () => {
  it('accepts a valid numeric field metadata object', () => {
    const field: ProfileFieldMetadata = {
      key: 'layer_height',
      label: 'Layer Height',
      fieldType: 'number',
      category: 'quality',
      isAdvanced: false,
      min: 0.04,
      max: 0.6,
      step: 0.01,
      unit: 'mm',
    };

    expect(field.key).toBe('layer_height');
    expect(field.label).toBe('Layer Height');
    expect(field.fieldType).toBe('number');
    expect(field.category).toBe('quality');
    expect(field.isAdvanced).toBe(false);
    expect(field.min).toBeDefined();
    expect(field.max).toBeDefined();
  });

  it('accepts a valid enum field metadata with options', () => {
    const options: EnumOption[] = [
      { value: 'grid', label: 'Grid' },
      { value: 'gyroid', label: 'Gyroid' },
    ];
    const field: ProfileFieldMetadata = {
      key: 'sparse_infill_pattern',
      label: 'Infill Pattern',
      fieldType: 'enum',
      category: 'strength',
      isAdvanced: false,
      options,
    };

    expect(field.fieldType).toBe('enum');
    expect(field.options).toBeDefined();
    expect(field.options!.length).toBe(2);
    expect(field.options![0].value).toBe('grid');
  });

  it('accepts a boolean field metadata', () => {
    const field: ProfileFieldMetadata = {
      key: 'enable_support',
      label: 'Enable Supports',
      fieldType: 'boolean',
      category: 'support',
      isAdvanced: false,
    };

    expect(field.fieldType).toBe('boolean');
    expect(field.options).toBeUndefined();
  });
});

describe('ProfileTypeSchema shape', () => {
  it('accepts a valid schema object', () => {
    const schema: ProfileTypeSchema = {
      profileType: 'process',
      categories: ['quality', 'strength', 'speed'],
      fields: [
        {
          key: 'layer_height',
          label: 'Layer Height',
          fieldType: 'number',
          category: 'quality',
          isAdvanced: false,
        },
      ],
    };

    expect(schema.profileType).toBe('process');
    expect(schema.categories.length).toBe(3);
    expect(schema.fields.length).toBe(1);
    expect(schema.fields[0].key).toBe('layer_height');
  });
});
