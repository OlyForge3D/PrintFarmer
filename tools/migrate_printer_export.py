#!/usr/bin/env python3
"""
Migration script to update PrintFarmer printer export JSON to include serverUrl and apiKey.
This script converts old printer exports (with only ipAddress) to the new format
that supports proper import with serverUrl.
"""

import json
import sys
from pathlib import Path

def migrate_printer_export(input_file: str, output_file: str = None) -> None:
    """
    Migrate old printer export format to new format with serverUrl, backendPort, and frontendPort.
    
    Old format: printers have ipAddress
    New format: printers have separate serverUrl (without port), backendPort, and frontendPort
    
    The new format excludes null properties from the export for cleaner JSON.
    """
    
    input_path = Path(input_file)
    if not input_path.exists():
        print(f"Error: Input file not found: {input_file}", file=sys.stderr)
        sys.exit(1)
    
    # Read the old JSON
    with open(input_path, 'r') as f:
        printers = json.load(f)
    
    if not isinstance(printers, list):
        print("Error: Expected JSON array of printers", file=sys.stderr)
        sys.exit(1)
    
    # Migrate each printer
    migrated = []
    for printer in printers:
        migrated_printer = {
            "printerId": printer.get("printerId"),
            "printerName": printer.get("printerName"),
            "printerModel": printer.get("printerModel"),
        }
        
        # Add optional fields if present (only non-null values to match new export format)
        if "capabilities" in printer and printer["capabilities"] is not None:
            cap = printer["capabilities"]
            # Exclude redundant printerId/printerName and null values from capabilities
            migrated_cap = {}
            for key, value in cap.items():
                # Skip redundant fields (already at top level)
                if key in ("printerId", "printerName"):
                    continue
                # Only include non-null values
                if value is not None:
                    migrated_cap[key] = value
            if migrated_cap:  # Only add if there's content
                migrated_printer["capabilities"] = migrated_cap
        
        if "manufacturerName" in printer and printer.get("manufacturerName"):
            migrated_printer["manufacturerName"] = printer["manufacturerName"]
        
        if "backend" in printer and printer.get("backend") is not None:
            migrated_printer["backend"] = printer["backend"]
        
        if "ipAddress" in printer and printer["ipAddress"]:
            # Separate serverUrl (without port) and backendPort
            ip = printer["ipAddress"]
            backend = printer.get("backend", 0)  # Default to Moonraker (0)
            migrated_printer["serverUrl"] = f"http://{ip}"  # Base URL without port
            migrated_printer["backendPort"] = 7125  # Default Moonraker port
            
            # Set frontendPort based on backend type
            # Moonraker (0): port 80, PrusaLink (1): port 5000 or 443
            if backend == 0:  # Moonraker
                migrated_printer["frontendPort"] = 80
            elif backend == 1:  # PrusaLink
                migrated_printer["frontendPort"] = 5000
            
            migrated_printer["ipAddress"] = ip
        
        # Only add apiKey and notes if they have values (new export format excludes nulls)
        # This makes the JSON cleaner and import more forgiving
        
        migrated.append(migrated_printer)
    
    # Determine output file
    if output_file is None:
        output_file = input_path.parent / f"{input_path.stem}-migrated.json"
    
    # Write the migrated JSON
    with open(output_file, 'w') as f:
        json.dump(migrated, f, indent=2)
    
    print(f"✅ Migration complete!")
    print(f"   Input:  {input_file}")
    print(f"   Output: {output_file}")
    print(f"   Migrated {len(migrated)} printers")
    print()
    print("⚠️  Important notes:")
    print("   - serverUrl contains the base URL without port (e.g., http://192.168.1.100)")
    print("   - backendPort was set to 7125 (Moonraker default)")
    print("   - Review and adjust serverUrl and ports manually if needed")
    print("   - If your printer uses PrusaLink, set backendPort to 443 or 8009")
    print("   - If your printer has a frontend interface, set frontendPort accordingly")
    print("   - Null properties are excluded from the export (cleaner JSON)")
    print()

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python3 migrate_printer_export.py <input.json> [output.json]")
        print()
        print("Example:")
        print("  python3 migrate_printer_export.py printfarmer-printers-2025-11-04.json")
        sys.exit(1)
    
    input_file = sys.argv[1]
    output_file = sys.argv[2] if len(sys.argv) > 2 else None
    
    migrate_printer_export(input_file, output_file)
