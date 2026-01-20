# PrintFarmer Seed Data

This directory contains YAML configuration files for seeding the database with default manufacturers, printer models, filament types, and component definitions.

## Directory Structure

```
/data/seed/
├── manufacturers.yaml       # Manufacturer definitions
├── filament-types.yaml     # Filament type definitions with temperatures
├── printer-models.yaml     # Printer model definitions (to be created)
└── components/             # Component model definitions
    ├── hotends.yaml        # Hotend model definitions (to be created)
    ├── extruders.yaml      # Extruder model definitions (to be created)
    ├── toolheads.yaml      # Toolhead model definitions (to be created)
    └── nozzles.yaml        # Nozzle model definitions (to be created)
```

## YAML File Format

All YAML files use camelCase for property names and follow these conventions:

### manufacturers.yaml
```yaml
- name: Manufacturer Name
```

### filament-types.yaml
```yaml
- name: Filament Type Name
  defaultHotendTemp: 220
  defaultBedTemp: 60
  isAbrasive: false
  needsEnclosure: false
```

### printer-models.yaml (Future)
```yaml
- name: Printer Model Name
  manufacturer: Manufacturer Name
  buildVolume:
    x: 250
    y: 210
    z: 220
  defaultBackend: PrusaLink  # Moonraker, PrusaLink, OctoPrint, SDCP
  motionType: Cartesian      # Cartesian, CoreXY, Delta, etc.
  hasHeatedBed: true
  hasEnclosure: false
  supportsAutoLeveling: true
  multiMaterial: false
  numberOfExtruders: 1
  maxHotendTemp: 300
  maxBedTemp: 120
  maxPrintSpeed: 200
  supportedMaterials:
    - PLA
    - PETG
    - ABS
  toolheads:
    - name: Primary
      toolhead: Stock Toolhead
      hotend: Stock Hotend
      extruder: Stock Extruder
      nozzle: Brass Nozzle
  aliases:
    - slicerType: OrcaSlicer
      slicerModelName: Prusa MK4S
    - slicerType: PrusaSlicer
      slicerModelName: MK4S
```

### components/hotends.yaml (Future)
```yaml
- name: Hotend Name
  manufacturer: Manufacturer Name
  maxTemp: 300
  isHighFlow: false
  description: Description of hotend
  url: https://example.com/product
```

### components/extruders.yaml (Future)
```yaml
- name: Extruder Name
  manufacturer: Manufacturer Name
  gearRatio: "3:1"
  isDirectDrive: true
  description: Description of extruder
  url: https://example.com/product
```

### components/toolheads.yaml (Future)
```yaml
- name: Toolhead Name
  manufacturer: Manufacturer Name
  description: Description of toolhead
  url: https://example.com/product
  defaultHotend: Compatible Hotend Name
  defaultExtruder: Compatible Extruder Name
  defaultNozzle: Compatible Nozzle Name
```

### components/nozzles.yaml (Future)
```yaml
- name: Nozzle Name
  manufacturer: Manufacturer Name
  maxTemp: 300
  nozzleType: Brass  # Brass, HardenedSteel, Ruby, etc.
  description: Description of nozzle
  url: https://example.com/product
```

## Customization

To customize seed data for your deployment:

1. Edit the YAML files in this directory
2. Restart the application or use the admin API to reload seed data
3. Changes only affect new/empty databases or explicit reloads

## Validation

All YAML files are validated on load:
- Required fields must be present
- References between files (e.g., manufacturer names) are resolved automatically
- Malformed YAML produces clear error messages in application logs

## Future Features

- Admin API endpoints for importing/exporting seed data as JSON
- Web UI for managing seed data
- Community-shared printer/component profiles
