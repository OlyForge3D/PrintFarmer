#!/usr/bin/env python3
"""
Extract OrcaSlicer setting metadata from PrintConfig.cpp and Tab.cpp.

Parses:
- PrintConfig.cpp: label, tooltip, sidetext (unit), min, max, mode, type, default
- Tab.cpp: tab layout (which fields on which tab/section), section icons

Outputs a JSON file that the frontend can consume for metadata-driven rendering.

Usage:
  python3 tools/extract-orca-metadata.py <orcaslicer-src-path> [--output <path>]

Example:
  python3 tools/extract-orca-metadata.py /path/to/orcaslicer/src --output src/Web/ReactApp/src/features/slicer/generated/orcaSettingsMetadata.json
"""

import re
import json
import sys
import os
from pathlib import Path
from typing import Optional

# ── PrintConfig.cpp parser ─────────────────────────────────────────────────

# Maps OrcaSlicer C++ config types to simpler type strings
TYPE_MAP = {
    'coFloat': 'float', 'coFloats': 'float',
    'coInt': 'int', 'coInts': 'int',
    'coBool': 'bool', 'coBools': 'bool',
    'coString': 'string', 'coStrings': 'string',
    'coPercent': 'percent', 'coPercents': 'percent',
    'coFloatOrPercent': 'float_or_percent',
    'coEnum': 'enum',
    'coPoint': 'point', 'coPoints': 'point',
}

MODE_MAP = {
    'comSimple': 'simple',
    'comAdvanced': 'advanced',
    'comDevelop': 'developer',
}


def extract_l_string(text: str) -> str:
    """Extract content from L("...") or plain "..." strings, handling multi-line concatenation."""
    # Handle L("string1" "string2") pattern (C++ string literal concatenation)
    pattern = r'L\(\s*"((?:[^"\\]|\\.)*)"\s*(?:"((?:[^"\\]|\\.)*)")?\s*\)'
    match = re.search(pattern, text)
    if match:
        result = match.group(1)
        if match.group(2):
            result += match.group(2)
        return result.replace('\\"', '"').replace('\\n', '\n')
    # Plain string
    match = re.search(r'"((?:[^"\\]|\\.)*)"', text)
    return match.group(1).replace('\\"', '"') if match else ''


def extract_default_value(line: str) -> Optional[str]:
    """Extract default value from set_default_value line."""
    # ConfigOptionFloat{0.5}
    m = re.search(r'ConfigOption\w+\s*\{\s*([^}]+)\s*\}', line)
    if m:
        val = m.group(1).strip().strip('"')
        return val
    # ConfigOptionBool(false)
    m = re.search(r'ConfigOption\w+\s*\(\s*([^)]+)\s*\)', line)
    if m:
        return m.group(1).strip()
    return None


def strip_block_comments(source: str) -> str:
    """Remove C-style block comments (/* ... */) from source, preserving line count."""
    result = []
    i = 0
    in_block = False
    while i < len(source):
        if in_block:
            if source[i:i+2] == '*/':
                in_block = False
                i += 2
            else:
                # Preserve newlines so line numbers stay correct
                if source[i] == '\n':
                    result.append('\n')
                i += 1
        else:
            if source[i:i+2] == '/*':
                in_block = True
                i += 2
            elif source[i:i+2] == '//':
                # Skip to end of line (single-line comment)
                while i < len(source) and source[i] != '\n':
                    i += 1
            else:
                result.append(source[i])
                i += 1
    return ''.join(result)


def parse_print_config(filepath: str) -> dict:
    """Parse PrintConfig.cpp and extract all setting definitions."""
    with open(filepath, 'r', encoding='utf-8') as f:
        content = strip_block_comments(f.read())

    settings = {}

    # Split into definition blocks using regex
    # Each block starts with: def = this->add("name", coType) or def = this->add_nullable(...)
    add_pattern = re.compile(r'def\s*=\s*this->add(?:_nullable)?\(\s*"([^"]+)"\s*,\s*(\w+)\s*\)')

    lines = content.split('\n')
    # Find all definition start lines (comments already stripped)
    block_starts = []
    for i, line in enumerate(lines):
        stripped = line.strip()
        m = add_pattern.search(stripped)
        if m:
            block_starts.append((i, m.group(1), m.group(2)))

    # Process each block
    for idx, (start_line, name, co_type) in enumerate(block_starts):
        end_line = block_starts[idx + 1][0] if idx + 1 < len(block_starts) else min(start_line + 50, len(lines))

        entry = {
            'key': name,
            'type': TYPE_MAP.get(co_type, co_type),
            'coType': co_type,
        }

        # Join all lines in this block
        block_text = '\n'.join(lines[start_line:end_line])

        # Extract label
        m = re.search(r'def->label\s*=\s*L\(\s*"((?:[^"\\]|\\.)*)"\s*\)', block_text)
        if m:
            entry['label'] = m.group(1).replace('\\"', '"')

        # Extract tooltip (may span multiple lines with string concatenation)
        m = re.search(r'def->tooltip\s*=\s*L\(\s*"((?:[^"\\]|\\.)*(?:"\s*"(?:[^"\\]|\\.)*)*)"\s*\)', block_text, re.DOTALL)
        if m:
            tooltip = m.group(1).replace('\\"', '"').replace('"\n"', '').replace('"  "', '').replace('\n', ' ')
            # Clean up C++ string concatenation artifacts
            tooltip = re.sub(r'"\s+"', '', tooltip)
            entry['tooltip'] = tooltip.strip()

        # Extract sidetext (unit)
        m = re.search(r'def->sidetext\s*=\s*L\(\s*"((?:[^"\\]|\\.)*)"\s*\)', block_text)
        if m:
            entry['unit'] = m.group(1)
        else:
            # Try non-L() sidetext
            m = re.search(r'def->sidetext\s*=\s*"((?:[^"\\]|\\.)*)"\s*;', block_text)
            if m:
                entry['unit'] = m.group(1)

        # Extract min
        m = re.search(r'def->min\s*=\s*(-?[\d.]+)', block_text)
        if m:
            val = m.group(1)
            entry['min'] = float(val) if '.' in val else int(val)

        # Extract max  
        m = re.search(r'def->max\s*=\s*(-?[\d.]+)', block_text)
        if m:
            val = m.group(1)
            entry['max'] = float(val) if '.' in val else int(val)

        # Extract mode
        for mode_cpp, mode_str in MODE_MAP.items():
            if f'def->mode = {mode_cpp}' in block_text or f'def->mode={mode_cpp}' in block_text:
                entry['mode'] = mode_str
                break

        # Extract category
        m = re.search(r'def->category\s*=\s*L\(\s*"((?:[^"\\]|\\.)*)"\s*\)', block_text)
        if m:
            entry['category'] = m.group(1)

        # Extract default value
        m = re.search(r'set_default_value\(new\s+ConfigOption\w+\s*[{(]\s*([^})]+)\s*[})]', block_text)
        if m:
            entry['default'] = m.group(1).strip().strip('"')

        # Extract gui_type
        if 'f_enum_open' in block_text:
            entry['gui_type'] = 'enum_open'
        elif 'GUIType::color' in block_text:
            entry['gui_type'] = 'color'

        # Check nullable
        if 'add_nullable' in lines[start_line]:
            entry['nullable'] = True

        # Only store if we got at least a label
        if 'label' not in entry:
            entry['label'] = name  # Use key as fallback

        settings[name] = entry

    return settings


# ── Tab.cpp parser ─────────────────────────────────────────────────────────

def parse_tab_layout(filepath: str, method_name: str = 'TabFilament::build') -> list:
    """Parse Tab.cpp to extract the tab/section/field layout for a specific tab class."""
    with open(filepath, 'r', encoding='utf-8') as f:
        content = strip_block_comments(f.read())

    # Find the method
    start = content.find(f'void {method_name}()')
    if start < 0:
        print(f"Warning: {method_name} not found in {filepath}", file=sys.stderr)
        return []

    # Extract until next top-level void
    rest = content[start:]
    brace_depth = 0
    end = 0
    for i, ch in enumerate(rest):
        if ch == '{':
            brace_depth += 1
        elif ch == '}':
            brace_depth -= 1
            if brace_depth == 0:
                end = i
                break
    method_body = rest[:end + 1]
    lines = method_body.split('\n')

    tabs = []
    current_tab = None
    current_section = None

    for line in lines:
        stripped = line.strip()

        # Skip comments
        if stripped.startswith('//'):
            continue

        # Tab page: add_options_page(L("name"), "icon_id")
        m = re.search(r'add_options_page\(\s*L\(\s*"([^"]+)"\s*\)\s*,\s*"([^"]*)"', stripped)
        if m:
            current_tab = {
                'name': m.group(1),
                'icon': m.group(2),
                'sections': [],
            }
            tabs.append(current_tab)
            current_section = None
            continue

        # Section: page->new_optgroup(L("name"), L"icon_id") or page->new_optgroup(L("name"), "icon_id")
        m = re.search(r'new_optgroup\(\s*L\(\s*"([^"]+)"\s*\)\s*(?:,\s*(?:L"([^"]*)"|\s*"([^"]*)"))?', stripped)
        if m and current_tab:
            icon = m.group(2) or m.group(3) or ''
            current_section = {
                'name': m.group(1),
                'icon': icon,
                'fields': [],
            }
            current_tab['sections'].append(current_section)
            continue

        # Single field: append_single_option_line("field_name", ...)
        m = re.search(r'append_single_option_line\(\s*"([^"]+)"', stripped)
        if m and current_section:
            current_section['fields'].append({
                'key': m.group(1),
                'compound': False,
            })
            continue

        # Compound line field: get_option("field_name", ...) or Option{"field_name", ...}
        m = re.search(r'get_option\(\s*"([^"]+)"', stripped)
        if m and current_section:
            current_section['fields'].append({
                'key': m.group(1),
                'compound': True,
            })
            continue

        m = re.search(r'Option\s*\{\s*"([^"]+)"', stripped)
        if m and current_section:
            current_section['fields'].append({
                'key': m.group(1),
                'compound': True,
            })
            continue

        # append_line variant
        m = re.search(r'append_line\(\s*(\w+)\s*\)', stripped)
        if m:
            continue

    return tabs


def parse_filament_tabs(filepath: str) -> list:
    """Parse filament tab layout including overrides page."""
    tabs = parse_tab_layout(filepath, 'TabFilament::build')
    overrides = parse_tab_layout(filepath, 'TabFilament::add_filament_overrides_page')
    if overrides:
        tabs.extend(overrides)
    return tabs


# ── Icon extractor ─────────────────────────────────────────────────────────

def find_icon_files(orca_root: str) -> dict:
    """Map icon IDs to SVG file paths (relative to resources/images)."""
    images_dir = os.path.join(orca_root, '..', 'resources', 'images')
    if not os.path.isdir(images_dir):
        # Try alternate path
        images_dir = os.path.join(orca_root, 'resources', 'images')
    if not os.path.isdir(images_dir):
        return {}

    icons = {}
    for f in sorted(os.listdir(images_dir)):
        if f.endswith('.svg'):
            icon_id = f[:-4]  # Remove .svg
            icons[icon_id] = f
    return icons


# ── Main ───────────────────────────────────────────────────────────────────

def main():
    if len(sys.argv) < 2:
        print(f"Usage: {sys.argv[0]} <orcaslicer-src-path> [--output <path>]", file=sys.stderr)
        sys.exit(1)

    src_path = sys.argv[1]
    output_path = None
    if '--output' in sys.argv:
        idx = sys.argv.index('--output')
        if idx + 1 < len(sys.argv):
            output_path = sys.argv[idx + 1]

    print_config_path = os.path.join(src_path, 'libslic3r', 'PrintConfig.cpp')
    tab_path = os.path.join(src_path, 'slic3r', 'GUI', 'Tab.cpp')

    if not os.path.exists(print_config_path):
        print(f"Error: {print_config_path} not found", file=sys.stderr)
        sys.exit(1)

    # Parse PrintConfig.cpp for field metadata
    print(f"Parsing {print_config_path}...", file=sys.stderr)
    all_settings = parse_print_config(print_config_path)
    print(f"  Found {len(all_settings)} total settings", file=sys.stderr)

    # Parse Tab.cpp for filament layout
    if os.path.exists(tab_path):
        print(f"Parsing {tab_path} for filament tab layout...", file=sys.stderr)
        filament_tabs = parse_filament_tabs(tab_path)
        machine_tabs = parse_tab_layout(tab_path, 'TabPrinter::build_fff')
        print(f"  Found {len(filament_tabs)} filament tabs, {len(machine_tabs)} machine tabs", file=sys.stderr)
    else:
        filament_tabs = []
        machine_tabs = []

    # Find icons
    icons = find_icon_files(src_path)
    print(f"  Found {len(icons)} icon SVGs", file=sys.stderr)

    # Collect filament-related settings (those appearing in filament tabs)
    filament_keys = set()
    for tab in filament_tabs:
        for section in tab['sections']:
            for field in section['fields']:
                filament_keys.add(field['key'])

    # Also include known filament prefixed settings not in tabs
    for key in all_settings:
        if key.startswith('filament_') or key.startswith('nozzle_temperature') or key.startswith('hot_plate') or key.startswith('cool_plate') or key.startswith('eng_plate') or key.startswith('textured_') or key.startswith('supertack_'):
            filament_keys.add(key)

    # Build filament metadata
    filament_metadata = {}
    for key in sorted(filament_keys):
        if key in all_settings:
            filament_metadata[key] = all_settings[key]
        else:
            filament_metadata[key] = {'key': key, 'label': key, 'type': 'unknown'}

    # Build machine-related settings
    machine_keys = set()
    for tab in machine_tabs:
        for section in tab['sections']:
            for field in section['fields']:
                machine_keys.add(field['key'])
    for key in all_settings:
        if key.startswith('machine_') or key.startswith('retraction_') or key.startswith('retract_') or key.startswith('wipe'):
            machine_keys.add(key)

    machine_metadata = {}
    for key in sorted(machine_keys):
        if key in all_settings:
            machine_metadata[key] = all_settings[key]

    result = {
        '_meta': {
            'source': 'OrcaSlicer PrintConfig.cpp + Tab.cpp',
            'generator': 'tools/extract-orca-metadata.py',
            'totalSettings': len(all_settings),
            'filamentSettings': len(filament_metadata),
            'machineSettings': len(machine_metadata),
        },
        'filament': {
            'tabs': filament_tabs,
            'settings': filament_metadata,
        },
        'machine': {
            'tabs': machine_tabs,
            'settings': machine_metadata,
        },
        'icons': {k: v for k, v in icons.items() if k.startswith('param_') or k.startswith('custom-gcode_')},
    }

    output = json.dumps(result, indent=2, ensure_ascii=False)

    if output_path:
        os.makedirs(os.path.dirname(output_path), exist_ok=True)
        with open(output_path, 'w', encoding='utf-8') as f:
            f.write(output)
        print(f"Written to {output_path}", file=sys.stderr)
    else:
        print(output)


if __name__ == '__main__':
    main()
