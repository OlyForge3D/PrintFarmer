# ProfileParserTester

A lightweight test harness for quickly validating and debugging OrcaSlicer profile parsing without running the full OrcaSlicer worker.

## Purpose

This tool uses the actual `OrcaProfilesService` from the OrcaSlicer worker to:
- **Test profile discovery** - Verify that profiles are correctly discovered from `~/.config/OrcaSlicer/`
- **Validate parsing** - Check that profile JSON is parsed into typed DTOs
- **Debug parsing issues** - Quickly iterate on parsing problems outside the full worker
- **Inspect individual profiles** - Parse and display details of specific profile files

## Architecture

The tester:
1. Registers the real `OrcaProfilesService` from the worker
2. Loads profiles from the system OrcaSlicer installation
3. Displays profile counts and samples
4. Can parse and inspect individual profile files

This is NOT a duplicate parser - it uses the exact same parsing code as the worker.

## Building

```bash
cd /home/pi/pfarm
export PATH="$HOME/.dotnet:$PATH"
dotnet build ProfileParserTester/ProfileParserTester.csproj -c Debug
```

## Running

### List all profiles

```bash
export PATH="$HOME/.dotnet:$PATH"
cd /home/pi/pfarm
dotnet run --project ProfileParserTester/ProfileParserTester.csproj
```

Output:
```
🔍 OrcaSlicer Profile Parser Test Harness
=========================================

📊 Profile Summary:
   Machine profiles:  5
   Filament profiles: 12
   Process profiles:  8
   Total:             25

🖨️  Machine Profiles:
────────────────────────────────────────────────────────────────────────────────
  • Prusa MK4S                                  (Manufacturer: Prusa Research)
  • Prusa i3 MK3S+                              (Manufacturer: Prusa Research)
  ...
```

### Inspect a specific profile

```bash
dotnet run --project ProfileParserTester/ProfileParserTester.csproj -- /path/to/profile.json
```

## Profile Types

The tester discovers and displays three profile categories:

### 🖨️ Machine Profiles
Located in: `~/.config/OrcaSlicer/profiles/printer/`
- Printer hardware configuration
- Nozzle diameter, build volume, etc.
- Manufacturer information

### 🧵 Filament Profiles
Located in: `~/.config/OrcaSlicer/profiles/filament/`
- Material type (PLA, PETG, TPU, etc.)
- Temperatures (nozzle, bed)
- Print speeds

### ⚙️ Process Profiles
Located in: `~/.config/OrcaSlicer/profiles/process/`
- Layer heights
- Infill percentages
- Support settings
- Quality classification (draft, standard, fine)

## When to Use This

1. **Debugging profile import issues** - Run this to confirm profiles load correctly from OrcaSlicer
2. **Testing new profile types** - Add a new profile to OrcaSlicer and verify it's discovered
3. **Validating parsing changes** - After modifying `OrcaProfilesService`, test parsing in isolation
4. **Integration tests** - Profile discovery without full worker startup (faster feedback)

## How It Works

1. Creates a minimal DI container with the necessary services
2. Instantiates `OrcaProfilesService` with a `NullLoggingService`
3. Calls the three `ListAvailable*ProfilesAsync()` methods
4. Displays the parsed profiles and their properties
5. Optionally inspects raw JSON of a specific profile

## Related Code

- **Parser**: `src/orcaslicer-worker/Services/OrcaProfilesService.cs`
- **DTOs**: 
  - `MachineProfileDto` in `src/shared/Farm.Web.Shared.csproj`
  - `FilamentProfileDto` in `src/shared/Farm.Web.Shared.csproj`
  - `ProcessProfileDto` in `src/shared/Farm.Web.Shared.csproj`
