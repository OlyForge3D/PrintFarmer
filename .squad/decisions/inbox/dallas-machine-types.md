# Decision: Machine Settings Types

**Author:** Dallas (frontend)
**Date:** 2025-07-18
**Task:** PFarm1-pysq.3

## Key Decisions

### 1. 105 unique keys (not 125)
The metadata JSON has 106 field entries but `fan_speedup_time` is listed twice (same key, two sections in the Cooling Fan group). Deduplicated to **105 unique keys** in the interface. The `_meta.machineSettings: 125` count in the JSON appears to include additional internal-only keys not represented in the tab structure.

### 2. Compound fields typed as `string`
All fields marked `"compound": true` in metadata (G-code macros, bed_exclude_area, extruder_printable_area, fan_speedup_time/overhangs, resonance speeds, thumbnails, printer_notes) are typed as `string` since OrcaSlicer serialises them as semicolon-delimited strings internally.

### 3. Simple vs Advanced split
15 settings classified as `simple` — printable_height, bed_exclude_area, support_multi_bed_types, gcode_flavor, nozzle_type, nozzle_diameter, extruder_printable_area, min/max_layer_height, retraction_length, retraction_speed, machine_max_speed_x/y/z/e. Everything else is `advanced`.

### 4. Default values source
Defaults based on a generic Ender-3 class printer (220×220×250, Marlin, 0.4mm brass nozzle, i3 structure). Multi-material parameters use OrcaSlicer's own compiled defaults.

### 5. Pattern alignment
File structure mirrors `slicerSettingsTypes.ts` exactly — same section comment style, same export pattern, augmented with MODE_MAP / CATEGORY_MAP / DEFAULT objects that the process file didn't yet have.
