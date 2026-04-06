import { describe, it, expect } from 'vitest';
import {
  DEFAULT_BASIC_FILAMENT_SETTINGS,
  DEFAULT_ADVANCED_FILAMENT_SETTINGS,
  FILAMENT_SETTING_TO_CATEGORY_MAP,
  MATERIAL_PRESETS,
} from '@/features/slicer/components/settings/filamentSettingsTypes';
import type { FilamentSettingsCategory } from '@/features/slicer/components/settings/filamentSettingsTypes';
import {
  DEFAULT_BASIC_SETTINGS,
  DEFAULT_SIMPLE_SETTINGS,
  DEFAULT_ADVANCED_SETTINGS,
  SETTING_TO_CATEGORY_MAP,
  INFILL_PATTERN_INFO,
  BED_ADHESION_INFO,
  isValidCategory,
} from '@/features/slicer/components/settings/slicerSettingsTypes';
import type { SettingsCategory } from '@/features/slicer/components/settings/slicerSettingsTypes';
import {
  DEFAULT_BASIC_MACHINE_SETTINGS,
  DEFAULT_ADVANCED_MACHINE_SETTINGS,
  MACHINE_SETTING_TO_CATEGORY_MAP,
  GCODE_DIALECT_LABELS,
  MOTION_TYPE_LABELS,
  NOZZLE_TYPE_LABELS,
  BED_TYPE_LABELS,
  PROBE_TYPE_LABELS,
  PRINTER_PRESETS,
} from '@/features/slicer/components/settings/machineSettingsTypes';
import type { MachineSettingsCategory } from '@/features/slicer/components/settings/machineSettingsTypes';
import type { ProfileFieldMetadata, EnumOption, ProfileTypeSchema } from '@/types/api';

// ---------------------------------------------------------------------------
// FilamentSettingsTypes contract tests
// ---------------------------------------------------------------------------
describe('FilamentSettingsTypes', () => {
  describe('DEFAULT_BASIC_FILAMENT_SETTINGS', () => {
    it('contains all expected basic keys', () => {
      const expected = ['name', 'material', 'density', 'cost', 'nozzleTemperature', 'bedTemperature'];
      for (const key of expected) {
        expect(DEFAULT_BASIC_FILAMENT_SETTINGS).toHaveProperty(key);
      }
    });

    it('has numeric temperature values', () => {
      expect(typeof DEFAULT_BASIC_FILAMENT_SETTINGS.nozzleTemperature).toBe('number');
      expect(typeof DEFAULT_BASIC_FILAMENT_SETTINGS.bedTemperature).toBe('number');
    });
  });

  describe('DEFAULT_ADVANCED_FILAMENT_SETTINGS', () => {
    const advancedKeys = [
      'nozzleTemperature', 'bedTemperature', 'flowRatio', 'density',
      'retractionLength', 'retractionSpeed', 'enableFanCooling',
      'minFanSpeed', 'maxFanSpeed', 'pressureAdvance',
      'enablePressureAdvance', 'startGcode', 'endGcode',
    ];

    it('contains all expected advanced keys', () => {
      for (const key of advancedKeys) {
        expect(DEFAULT_ADVANCED_FILAMENT_SETTINGS).toHaveProperty(key);
      }
    });

    it('inherits basic settings', () => {
      expect(DEFAULT_ADVANCED_FILAMENT_SETTINGS.nozzleTemperature)
        .toBe(DEFAULT_BASIC_FILAMENT_SETTINGS.nozzleTemperature);
      expect(DEFAULT_ADVANCED_FILAMENT_SETTINGS.bedTemperature)
        .toBe(DEFAULT_BASIC_FILAMENT_SETTINGS.bedTemperature);
    });
  });

  describe('FILAMENT_SETTING_TO_CATEGORY_MAP', () => {
    const validCategories: FilamentSettingsCategory[] = [
      'temperature', 'flow', 'cooling', 'retraction', 'physical', 'gcode', 'other',
    ];

    it('covers all expected categories', () => {
      const usedCategories = new Set(Object.values(FILAMENT_SETTING_TO_CATEGORY_MAP));
      for (const cat of ['temperature', 'flow', 'cooling', 'retraction', 'physical', 'gcode']) {
        expect(usedCategories.has(cat as FilamentSettingsCategory)).toBe(true);
      }
    });

    it('maps every field to a valid category', () => {
      for (const [, category] of Object.entries(FILAMENT_SETTING_TO_CATEGORY_MAP)) {
        expect(validCategories).toContain(category);
      }
    });

    it('maps temperature fields to temperature category', () => {
      expect(FILAMENT_SETTING_TO_CATEGORY_MAP['nozzleTemperature']).toBe('temperature');
      expect(FILAMENT_SETTING_TO_CATEGORY_MAP['bedTemperature']).toBe('temperature');
    });

    it('maps retraction fields to retraction category', () => {
      expect(FILAMENT_SETTING_TO_CATEGORY_MAP['retractionLength']).toBe('retraction');
      expect(FILAMENT_SETTING_TO_CATEGORY_MAP['retractionSpeed']).toBe('retraction');
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
        expect(preset.nozzleTemperature).toBeGreaterThan(0);
        expect(preset.bedTemperature).toBeGreaterThanOrEqual(0);
      }
    });
  });
});

// ---------------------------------------------------------------------------
// SlicerSettingsTypes contract tests
// ---------------------------------------------------------------------------
describe('SlicerSettingsTypes', () => {
  describe('DEFAULT_BASIC_SETTINGS', () => {
    it('contains core basic keys', () => {
      expect(DEFAULT_BASIC_SETTINGS).toHaveProperty('infillDensity');
      expect(DEFAULT_BASIC_SETTINGS).toHaveProperty('infillPattern');
      expect(DEFAULT_BASIC_SETTINGS).toHaveProperty('wallCount');
      expect(DEFAULT_BASIC_SETTINGS).toHaveProperty('bedAdhesion');
      expect(DEFAULT_BASIC_SETTINGS).toHaveProperty('enableSupports');
    });

    it('has reasonable default types', () => {
      expect(typeof DEFAULT_BASIC_SETTINGS.infillDensity).toBe('number');
      expect(typeof DEFAULT_BASIC_SETTINGS.wallCount).toBe('number');
      expect(typeof DEFAULT_BASIC_SETTINGS.enableSupports).toBe('boolean');
      expect(typeof DEFAULT_BASIC_SETTINGS.infillPattern).toBe('string');
    });
  });

  describe('DEFAULT_SIMPLE_SETTINGS', () => {
    it('extends basic with layer/line controls', () => {
      expect(DEFAULT_SIMPLE_SETTINGS).toHaveProperty('layerHeight');
      expect(DEFAULT_SIMPLE_SETTINGS).toHaveProperty('firstLayerHeight');
      expect(DEFAULT_SIMPLE_SETTINGS).toHaveProperty('lineWidthDefault');
      expect(DEFAULT_SIMPLE_SETTINGS).toHaveProperty('topLayers');
      expect(DEFAULT_SIMPLE_SETTINGS).toHaveProperty('bottomLayers');
    });

    it('inherits basic defaults', () => {
      expect(DEFAULT_SIMPLE_SETTINGS.infillDensity).toBe(DEFAULT_BASIC_SETTINGS.infillDensity);
    });
  });

  describe('DEFAULT_ADVANCED_SETTINGS', () => {
    it('has speed settings as numbers', () => {
      expect(typeof DEFAULT_ADVANCED_SETTINGS.printSpeed).toBe('number');
      expect(typeof DEFAULT_ADVANCED_SETTINGS.outerWallSpeed).toBe('number');
      expect(typeof DEFAULT_ADVANCED_SETTINGS.travelSpeed).toBe('number');
    });

    it('has support settings', () => {
      expect(DEFAULT_ADVANCED_SETTINGS).toHaveProperty('supportType');
      expect(DEFAULT_ADVANCED_SETTINGS).toHaveProperty('supportDensity');
      expect(DEFAULT_ADVANCED_SETTINGS).toHaveProperty('supportAngle');
    });
  });

  describe('SETTING_TO_CATEGORY_MAP', () => {
    const validCategories: SettingsCategory[] = [
      'quality', 'strength', 'speed', 'support', 'multimaterial', 'other',
    ];

    it('maps key process settings', () => {
      expect(SETTING_TO_CATEGORY_MAP['layerHeight']).toBe('quality');
      expect(SETTING_TO_CATEGORY_MAP['infillDensity']).toBe('strength');
      expect(SETTING_TO_CATEGORY_MAP['printSpeed']).toBe('speed');
      expect(SETTING_TO_CATEGORY_MAP['enableSupports']).toBe('support');
    });

    it('maps every field to a valid category', () => {
      for (const [, category] of Object.entries(SETTING_TO_CATEGORY_MAP)) {
        expect(validCategories).toContain(category);
      }
    });

    it('covers all category values', () => {
      const usedCategories = new Set(Object.values(SETTING_TO_CATEGORY_MAP));
      for (const cat of validCategories) {
        expect(usedCategories.has(cat)).toBe(true);
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
  describe('DEFAULT_BASIC_MACHINE_SETTINGS', () => {
    it('contains key machine fields', () => {
      expect(DEFAULT_BASIC_MACHINE_SETTINGS).toHaveProperty('name');
      expect(DEFAULT_BASIC_MACHINE_SETTINGS).toHaveProperty('buildVolumeX');
      expect(DEFAULT_BASIC_MACHINE_SETTINGS).toHaveProperty('buildVolumeY');
      expect(DEFAULT_BASIC_MACHINE_SETTINGS).toHaveProperty('buildVolumeZ');
      expect(DEFAULT_BASIC_MACHINE_SETTINGS).toHaveProperty('nozzleDiameter');
      expect(DEFAULT_BASIC_MACHINE_SETTINGS).toHaveProperty('maxPrintSpeed');
    });

    it('has positive build volume values', () => {
      expect(DEFAULT_BASIC_MACHINE_SETTINGS.buildVolumeX).toBeGreaterThan(0);
      expect(DEFAULT_BASIC_MACHINE_SETTINGS.buildVolumeY).toBeGreaterThan(0);
      expect(DEFAULT_BASIC_MACHINE_SETTINGS.buildVolumeZ).toBeGreaterThan(0);
    });
  });

  describe('DEFAULT_ADVANCED_MACHINE_SETTINGS', () => {
    it('inherits basic settings', () => {
      expect(DEFAULT_ADVANCED_MACHINE_SETTINGS.buildVolumeX)
        .toBe(DEFAULT_BASIC_MACHINE_SETTINGS.buildVolumeX);
      expect(DEFAULT_ADVANCED_MACHINE_SETTINGS.nozzleDiameter)
        .toBe(DEFAULT_BASIC_MACHINE_SETTINGS.nozzleDiameter);
    });

    it('has motion and capability fields', () => {
      expect(DEFAULT_ADVANCED_MACHINE_SETTINGS).toHaveProperty('motionType');
      expect(DEFAULT_ADVANCED_MACHINE_SETTINGS).toHaveProperty('maxAccelerationX');
      expect(DEFAULT_ADVANCED_MACHINE_SETTINGS).toHaveProperty('gcodeDialect');
      expect(DEFAULT_ADVANCED_MACHINE_SETTINGS).toHaveProperty('hasHeatedBed');
    });

    it('has gcode fields as strings', () => {
      expect(typeof DEFAULT_ADVANCED_MACHINE_SETTINGS.startGcode).toBe('string');
      expect(typeof DEFAULT_ADVANCED_MACHINE_SETTINGS.endGcode).toBe('string');
    });
  });

  describe('MACHINE_SETTING_TO_CATEGORY_MAP', () => {
    const validCategories: MachineSettingsCategory[] = [
      'general', 'extruder', 'printbed', 'capabilities', 'gcode',
    ];

    it('covers all expected categories', () => {
      const usedCategories = new Set(Object.values(MACHINE_SETTING_TO_CATEGORY_MAP));
      for (const cat of validCategories) {
        expect(usedCategories.has(cat)).toBe(true);
      }
    });

    it('maps every field to a valid category', () => {
      for (const [, category] of Object.entries(MACHINE_SETTING_TO_CATEGORY_MAP)) {
        expect(validCategories).toContain(category);
      }
    });

    it('maps key fields to correct categories', () => {
      expect(MACHINE_SETTING_TO_CATEGORY_MAP['name']).toBe('general');
      expect(MACHINE_SETTING_TO_CATEGORY_MAP['nozzleDiameter']).toBe('extruder');
      expect(MACHINE_SETTING_TO_CATEGORY_MAP['bedType']).toBe('printbed');
      expect(MACHINE_SETTING_TO_CATEGORY_MAP['hasHeatedBed']).toBe('capabilities');
      expect(MACHINE_SETTING_TO_CATEGORY_MAP['startGcode']).toBe('gcode');
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
        expect(preset.buildVolumeX).toBeGreaterThan(0);
        expect(preset.buildVolumeY).toBeGreaterThan(0);
        expect(preset.buildVolumeZ).toBeGreaterThan(0);
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
      key: 'layerHeight',
      label: 'Layer Height',
      fieldType: 'number',
      category: 'quality',
      isAdvanced: false,
      min: 0.04,
      max: 0.6,
      step: 0.01,
      unit: 'mm',
    };

    expect(field.key).toBe('layerHeight');
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
      key: 'infillPattern',
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
      key: 'enableSupports',
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
          key: 'layerHeight',
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
    expect(schema.fields[0].key).toBe('layerHeight');
  });
});
