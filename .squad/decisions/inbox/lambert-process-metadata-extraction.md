# Process Metadata Extraction — Audit & Improvements

**Bead:** PFarm1-d3by
**Author:** Lambert (Backend)
**Date:** 2025-07-25

## Summary

Audited `tools/extract-orca-metadata.py` against latest OrcaSlicer source (main branch).
Found and fixed one extraction gap; regenerated metadata JSON with improved completeness.

## Findings

### Process Metadata (TabPrint::build) — Previously 344, now 347

The process section was already well-covered with 6 tabs and 318 tab fields.
Three new settings from the latest OrcaSlicer source were picked up:

- `combine_brims` — new Quality/Others option
- `initial_layer_travel_acceleration` — new Speed option
- `initial_layer_travel_jerk` — new Speed option

All 6 tabs remain correct: Quality, Strength, Speed, Support, Multimaterial, Others.

### Machine Metadata (TabPrinter::build_fff) — 125 settings, 6 tabs ✅

All 6 machine tabs were already correctly extracted:
Basic information, Machine G-code, Multimaterial, Extruder, Motion ability, Notes.

**Bug fixed:** 12 axis-expanded settings (`machine_max_speed_x/y/z/e`,
`machine_max_acceleration_x/y/z/e`, `machine_max_jerk_x/y/z/e`) were present in
the tab field layout but missing from the settings dictionary. These settings are
defined in PrintConfig.cpp using a C++ for-loop with string concatenation:

```cpp
for (const AxisDefault &axis : axes) {
    def = this->add("machine_max_speed_" + axis.name, coFloats);
    def->full_label = (boost::format("Maximum speed %1%") % axis_upper).str();
    ...
}
```

The static regex parser (`def = this->add("literal_name", coType)`) couldn't match
the concatenated key. Added `_expand_printconfig_axis_loops()` to pre-process
PrintConfig.cpp, expanding the AxisDefault loop into 4 copies with literal strings.
All 12 axis settings now have full metadata (label, tooltip, unit, type, mode, min).

### Filament Metadata — Previously 108, now 110

Two new settings from latest OrcaSlicer source:
- `activate_air_filtration_during_print`
- `activate_air_filtration_on_completion`

## Changes Made

### `tools/extract-orca-metadata.py`

- Added `_expand_printconfig_axis_loops()` — detects the `for (const AxisDefault &axis : axes)`
  loop in PrintConfig.cpp and expands it into literal definitions for x/y/z/e
- Updated `parse_print_config()` to call the expansion before regex parsing
- Added fallback patterns for `def->full_label` and `def->tooltip` to match plain strings
  (not wrapped in `L()`) that result from the expansion

### `orcaSettingsMetadata.json`

Regenerated from latest OrcaSlicer source. Changes:
- `_meta.totalSettings`: 781 → 798
- `_meta.filamentSettings`: 108 → 110
- `_meta.processSettings`: 344 → 347
- `_meta.machineSettings`: 125 → 125 (same count but axis keys now have full metadata)
- 5 new settings added across filament/process
- 12 machine axis settings now have proper labels, tooltips, and units

## Edge Cases Noted

1. **Compound fields** — Some settings use `get_option()` / `Option{}` for multi-value
   lines (e.g., x+y dimensions). These are correctly tagged `compound: true` in the JSON.

2. **Conditional visibility** — OrcaSlicer's `toggle_options()` methods control field
   visibility based on other settings (e.g., support options hidden when support disabled).
   This is NOT captured in the metadata. Frontend must handle conditional visibility.

3. **Dynamic extruder tabs** — The Extruder tab is created per-extruder with
   `wxString::Format("Extruder %d", i+1)`. The script handles this by constructing
   a single canonical Extruder tab from known section names.

4. **Setting Overrides page** — The filament Setting Overrides tab has 0 fields in the
   tab layout because it's populated dynamically at runtime. This is expected.

## Validation

- ✅ JSON validates (`json.load()` succeeds)
- ✅ All tab field keys exist in their category's settings dict
- ✅ All 12 axis-expanded machine settings have label, tooltip, unit, type, mode, min
- ✅ Settings counts ≥ previous values (no regressions)
- ✅ React lint unaffected (pre-existing error in metadataTypes.ts, not related)
