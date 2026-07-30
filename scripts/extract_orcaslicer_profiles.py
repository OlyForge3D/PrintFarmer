#!/usr/bin/env python3
"""
Extract OrcaSlicer profiles with full inheritance chains.

This script extracts machine, process, and filament profiles from OrcaSlicer's
system profiles, resolving all inheritance chains. Supports extracting:
  - Single printer from single manufacturer
  - All printers from single manufacturer
  - All printers from all manufacturers

Usage:
  # Extract all manufacturers and all printers
  ./extract_profiles.py

  # Extract all printers from Prusa
  ./extract_profiles.py --manufacturer Prusa

  # Extract specific printer
  ./extract_profiles.py --manufacturer Prusa --printer "Prusa MK4S"

  # List available manufacturers
  ./extract_profiles.py --list-manufacturers

  # List printers in a manufacturer
  ./extract_profiles.py --manufacturer Prusa --list-printers

  # Custom OrcaSlicer profiles path
  ./extract_profiles.py --profiles-path /path/to/profiles
"""

import json
import argparse
from pathlib import Path
from typing import Dict, Any, Optional, List

def load_json(path: Path) -> Dict[str, Any]:
    """Load and parse JSON file."""
    if not path.exists():
        return {}
    with open(path) as f:
        return json.load(f)

def resolve_inheritance_chain(profile_dict: Dict[str, Any], profile_type: str, 
                              manufacturer: str, base_path: Path) -> Dict[str, Any]:
    """Recursively resolve inheritance chain for a profile."""
    result = dict(profile_dict)
    
    if "inherits" in result:
        parent_file = result["inherits"]
        parent_path = base_path / parent_file
        
        if parent_path.exists():
            parent_dict = load_json(parent_path)
            parent_resolved = resolve_inheritance_chain(parent_dict, profile_type, manufacturer, base_path)
            merged = dict(parent_resolved)
            merged.update(result)
            result = merged
    
    return result

def get_manufacturers(profiles_path: Path) -> List[str]:
    """Get list of available manufacturers."""
    manufacturers = []
    for item in profiles_path.iterdir():
        if item.is_file() and item.suffix == ".json" and item.stem != "system":
            manufacturers.append(item.stem)
    return sorted(manufacturers)

def get_printers(profiles_path: Path, manufacturer: str) -> List[str]:
    """Get list of printers for a manufacturer."""
    mfg_json = profiles_path / f"{manufacturer}.json"
    data = load_json(mfg_json)
    machines = data.get("machine_model_list", [])
    return sorted([m.get("name", "") for m in machines if m.get("name")])

def extract_machine_profile(profiles_path: Path, manufacturer: str, model_name: str) -> Optional[Dict[str, Any]]:
    """Extract complete machine profile with inheritance resolved."""
    profiles_dir = profiles_path / manufacturer
    if not profiles_dir.exists():
        return None
    
    mfg_json_path = profiles_path / f"{manufacturer}.json"
    mfg_data = load_json(mfg_json_path)
    
    machine_models = mfg_data.get("machine_model_list", [])
    
    machine_file = None
    for model in machine_models:
        if model.get("name") == model_name:
            machine_file = model.get("sub_path")
            break
    
    if not machine_file:
        return None
    
    machine_path = profiles_dir / machine_file
    if not machine_path.exists():
        return None
    
    machine_dict = load_json(machine_path)
    machine_dir = machine_path.parent
    machine_resolved = resolve_inheritance_chain(machine_dict, "machine", manufacturer, machine_dir)
    
    return machine_resolved

def extract_process_profiles(profiles_path: Path, manufacturer: str) -> List[Dict[str, Any]]:
    """Extract process profiles for manufacturer."""
    profiles_dir = profiles_path / manufacturer
    if not profiles_dir.exists():
        return []
    
    mfg_json_path = profiles_path / f"{manufacturer}.json"
    mfg_data = load_json(mfg_json_path)
    
    process_list = mfg_data.get("process_list", [])
    processes = []
    
    for process in process_list:
        process_file = process.get("sub_path")
        if process_file:
            process_path = profiles_dir / process_file
            if process_path.exists():
                process_dict = load_json(process_path)
                process_dir = process_path.parent
                process_resolved = resolve_inheritance_chain(process_dict, "process", manufacturer, process_dir)
                processes.append({
                    "name": process.get("name", "Unknown Process"),
                    "file": process_file,
                    "data": process_resolved
                })
    
    return processes

def extract_filament_profiles(profiles_path: Path, manufacturer: str) -> List[Dict[str, Any]]:
    """Extract filament profiles for manufacturer."""
    profiles_dir = profiles_path / manufacturer
    if not profiles_dir.exists():
        return []
    
    mfg_json_path = profiles_path / f"{manufacturer}.json"
    mfg_data = load_json(mfg_json_path)
    
    filament_list = mfg_data.get("filament_list", [])
    filaments = []
    
    for filament in filament_list:
        filament_file = filament.get("sub_path")
        if filament_file:
            filament_path = profiles_dir / filament_file
            if filament_path.exists():
                filament_dict = load_json(filament_path)
                filament_dir = filament_path.parent
                filament_resolved = resolve_inheritance_chain(filament_dict, "filament", manufacturer, filament_dir)
                filaments.append({
                    "name": filament.get("name", "Unknown Filament"),
                    "file": filament_file,
                    "data": filament_resolved
                })
    
    return filaments

def merge_into_library(extracted_data: Dict[str, Any], library_path: Path, is_index: bool = False) -> tuple[int, List[str]]:
    """
    Merge extracted profiles into existing library index.
    Args:
        extracted_data: Either full output dict (is_index=False) or index dict (is_index=True)
        library_path: Path to library Profiles directory
        is_index: If True, extracted_data is an index dict; if False, it's full output
    Returns: (count_added, list_of_added_profiles)
    """
    library_index_path = library_path / "index.json"
    if not library_index_path.exists():
        print(f"Error: Library index not found at {library_index_path}", file=__import__('sys').stderr)
        return 0, []
    
    # Load existing index
    with open(library_index_path) as f:
        existing = json.load(f)
    
    # Extract machines and processes from either format
    if is_index:
        machines = extracted_data.get("machines", [])
        processes = []
        filaments = {}
    else:
        machines = extracted_data.get("machines", [])
        processes = extracted_data.get("processes", [])
        filaments = extracted_data.get("filaments", {})
    
    # Track what's being added
    existing_ids = {m["id"] for m in existing["machines"]}
    added = []
    added_count = 0
    
    # Merge machines
    for extracted_machine in machines:
        machine_id = extracted_machine["id"]
        if machine_id not in existing_ids:
            # Add to index
            existing["machines"].append({
                "id": machine_id,
                "name": extracted_machine["name"],
                "manufacturer": extracted_machine["manufacturer"],
                "file": f"machines/{machine_id}.json"
            })
            added.append(f"{extracted_machine['manufacturer']} - {extracted_machine['name']}")
            added_count += 1
            
            # Copy machine profile file to library
            machines_dir = library_path / "machines"
            machines_dir.mkdir(parents=True, exist_ok=True)
            
            # Build machine profile data - handle both full output and index formats
            if "machine_profile" in extracted_machine:
                machine_config = extracted_machine["machine_profile"]
            else:
                machine_config = extracted_machine.get("machine", {})
            
            machine_filaments = filaments.get(extracted_machine["manufacturer"], [])
            machine_processes = [p for p in processes 
                               if p.get("printer") == extracted_machine["name"]]
            
            machine_profile = {
                "version": "2.4.0",
                "id": machine_id,
                "name": extracted_machine["name"],
                "manufacturer": extracted_machine["manufacturer"],
                "machine": machine_config,
                "processes": machine_processes,
                "filaments": machine_filaments,
                "lastUpdated": __import__('datetime').datetime.now().isoformat(),
                "source": extracted_data.get("source", "")
            }
            
            machine_file = machines_dir / f"{machine_id}.json"
            with open(machine_file, 'w') as f:
                json.dump(machine_profile, f, indent=2)
    
    # Sort machines for consistency
    existing["machines"].sort(key=lambda m: (m.get("manufacturer", ""), m.get("name", "")))
    
    # Update timestamp
    existing["lastUpdated"] = extracted_data.get("lastUpdated", __import__('datetime').datetime.now().isoformat())
    
    # Write merged index
    with open(library_index_path, 'w') as f:
        json.dump(existing, f, indent=2)
    
    return added_count, added

def main():
    parser = argparse.ArgumentParser(
        description="Extract OrcaSlicer profiles with inheritance resolution",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__
    )
    
    parser.add_argument(
        "--profiles-path",
        type=Path,
        default=Path("/Applications/Orcaslicer.app/Contents/Resources/profiles"),
        help="Path to OrcaSlicer profiles directory"
    )
    
    parser.add_argument(
        "--manufacturer",
        help="Manufacturer to extract (e.g., 'Prusa', 'Voron')"
    )
    
    parser.add_argument(
        "--printer",
        action="append",
        dest="printers",
        help="Specific printer model to extract (can be used multiple times, requires --manufacturer)"
    )
    
    parser.add_argument(
        "--list-manufacturers",
        action="store_true",
        help="List available manufacturers and exit"
    )
    
    parser.add_argument(
        "--list-printers",
        action="store_true",
        help="List printers for a manufacturer (requires --manufacturer)"
    )
    
    parser.add_argument(
        "--output",
        type=Path,
        help="Output JSON file (default: stdout)"
    )
    
    parser.add_argument(
        "--output-dir",
        type=Path,
        help="Output directory for individual machine profiles (creates index + machines/)"
    )
    
    parser.add_argument(
        "--library-path",
        type=Path,
        help="Path to library Profiles directory to auto-merge extracted profiles into existing index"
    )
    
    parser.add_argument(
        "--pretty",
        action="store_true",
        default=True,
        help="Pretty-print JSON output (default: True)"
    )
    
    args = parser.parse_args()
    
    # Validate profiles path
    if not args.profiles_path.exists():
        print(f"Error: Profiles path not found: {args.profiles_path}")
        return 1
    
    # List manufacturers
    if args.list_manufacturers:
        manufacturers = get_manufacturers(args.profiles_path)
        print("Available manufacturers:")
        for mfg in manufacturers:
            print(f"  - {mfg}")
        return 0
    
    # List printers
    if args.list_printers:
        if not args.manufacturer:
            print("Error: --manufacturer required for --list-printers")
            return 1
        printers = get_printers(args.profiles_path, args.manufacturer)
        print(f"Printers for {args.manufacturer}:")
        for printer in printers:
            print(f"  - {printer}")
        return 0
    
    # Determine what to extract
    if args.manufacturer and args.printers:
        # Extract specific printers from manufacturer
        manufacturers = {args.manufacturer: args.printers}
    elif args.manufacturer:
        # Extract all printers from manufacturer
        printers = get_printers(args.profiles_path, args.manufacturer)
        manufacturers = {args.manufacturer: printers}
    else:
        # Extract all manufacturers and printers
        all_manufacturers = get_manufacturers(args.profiles_path)
        manufacturers = {mfg: get_printers(args.profiles_path, mfg) for mfg in all_manufacturers}
    
    # Extract profiles
    all_machines = []
    all_processes = []
    all_filaments = {}
    
    total_machines = sum(len(printers) for printers in manufacturers.values())
    current = 0
    
    for manufacturer, printers in manufacturers.items():
        print(f"Processing {manufacturer}...", file=__import__('sys').stderr)
        
        # Extract filaments once per manufacturer
        if manufacturer not in all_filaments:
            filaments = extract_filament_profiles(args.profiles_path, manufacturer)
            if filaments:
                all_filaments[manufacturer] = filaments
        
        # Extract machines and processes
        for printer in printers:
            current += 1
            print(f"  [{current}/{total_machines}] {printer}...", file=__import__('sys').stderr)
            
            machine = extract_machine_profile(args.profiles_path, manufacturer, printer)
            if machine:
                all_machines.append({
                    "id": f"{manufacturer.lower()}_{printer.lower().replace(' ', '_').replace('+', 'plus').replace('.', '')}",
                    "name": printer,
                    "manufacturer": manufacturer,
                    "machine_profile": machine
                })
            
            processes = extract_process_profiles(args.profiles_path, manufacturer)
            if processes:
                for p in processes:
                    all_processes.append({
                        "id": f"process_{manufacturer.lower()}_{printer.lower().replace(' ', '_').replace('+', 'plus').replace('.', '')}_{p['name'].lower().replace(' ', '_').replace('-', '_')}",
                        "name": f"{p['name']} ({printer})",
                        "manufacturer": manufacturer,
                        "printer": printer,
                        "process_profile": p['data']
                    })
    
    # Build output
    output = {
        "version": "2.4.0",
        "description": "OrcaSlicer v2.4.0 Official Profiles - Extracted with inheritance resolved",
        "lastUpdated": __import__('datetime').datetime.now().isoformat(),
        "source": str(args.profiles_path),
        "machines": all_machines,
        "processes": all_processes,
        "filaments": all_filaments
    }
    
    # Output results
    print(f"\nExtraction complete:", file=__import__('sys').stderr)
    print(f"  Machines: {len(all_machines)}", file=__import__('sys').stderr)
    print(f"  Processes: {len(all_processes)}", file=__import__('sys').stderr)
    print(f"  Filament types: {sum(len(v) for v in all_filaments.values())}", file=__import__('sys').stderr)
    
    # Handle directory output (individual machine files + index)
    if args.output_dir:
        args.output_dir.mkdir(parents=True, exist_ok=True)
        machines_dir = args.output_dir / "machines"
        machines_dir.mkdir(exist_ok=True)
        
        # Create index with machine metadata
        index = {
            "version": "2.4.0",
            "description": "OrcaSlicer v2.4.0 Official Profiles Index",
            "lastUpdated": __import__('datetime').datetime.now().isoformat(),
            "source": str(args.profiles_path),
            "machines": [
                {
                    "id": m["id"],
                    "name": m["name"],
                    "manufacturer": m["manufacturer"],
                    "file": f"machines/{m['id']}.json"
                }
                for m in all_machines
            ]
        }
        
        # Write index
        index_path = args.output_dir / "index.json"
        with open(index_path, 'w') as f:
            json.dump(index, f, indent=2 if args.pretty else None)
        print(f"  Index written to: {index_path}", file=__import__('sys').stderr)
        
        # Write individual machine profiles
        for machine in all_machines:
            machine_id = machine["id"]
            # Get processes and filaments for this machine
            machine_processes = [p for p in all_processes if p.get("printer") == machine["name"]]
            machine_filaments = all_filaments.get(machine["manufacturer"], [])
            
            machine_profile = {
                "version": "2.4.0",
                "id": machine_id,
                "name": machine["name"],
                "manufacturer": machine["manufacturer"],
                "machine": machine["machine_profile"],
                "processes": machine_processes,
                "filaments": machine_filaments,
                "lastUpdated": __import__('datetime').datetime.now().isoformat(),
                "source": str(args.profiles_path)
            }
            
            machine_file = machines_dir / f"{machine_id}.json"
            with open(machine_file, 'w') as f:
                json.dump(machine_profile, f, indent=2 if args.pretty else None)
        
        print(f"  {len(all_machines)} machine profiles written to: {machines_dir}", file=__import__('sys').stderr)
        
        # Auto-merge into library if --library-path provided
        if args.library_path:
            if not args.library_path.exists():
                print(f"Error: Library path not found: {args.library_path}", file=__import__('sys').stderr)
                return 1
            
            print(f"\nMerging into library: {args.library_path}", file=__import__('sys').stderr)
            added_count, added_profiles = merge_into_library(output, args.library_path, is_index=False)
            
            if added_count > 0:
                print(f"✅ Merged index updated: {added_count} new profile(s)", file=__import__('sys').stderr)
                for profile in added_profiles:
                    print(f"   ✓ Added: {profile}", file=__import__('sys').stderr)
                print(f"\n📁 Library updated: {args.library_path}", file=__import__('sys').stderr)
            else:
                print(f"ℹ️  No new profiles to add (all already in library)", file=__import__('sys').stderr)
        
        return 0
    
    json_output = json.dumps(output, indent=2 if args.pretty else None)
    
    if args.output:
        with open(args.output, 'w') as f:
            f.write(json_output)
        print(f"  Written to: {args.output}", file=__import__('sys').stderr)
    else:
        print(json_output)
    
    return 0

if __name__ == "__main__":
    exit(main())
