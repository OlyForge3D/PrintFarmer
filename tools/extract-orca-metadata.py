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


def _clean_default_value(raw: str) -> Optional[str]:
    """Normalize a raw C++ default value, returning None if unparseable."""
    if not raw:
        return None
    # Drop C++ type qualifiers and casts: "(int)" prefix, "ConfigOption*::nil_value(" etc.
    if 'ConfigOption' in raw and 'nil_value' in raw:
        return None  # nullable — no meaningful default
    if raw.startswith('(int'):
        return None  # C++ cast expression, not a real default
    # C++ enum references: "ntUndefine", "RetractLiftEnforceType ::rletAllSurfaces", "ZHopType::zhtSlope"
    enum_m = re.search(r'(\w+)\s*::\s*(\w+)', raw)
    if enum_m:
        raw = enum_m.group(2)  # keep only the enumerator name
    # L("text") — localized string wrapper
    l_m = re.search(r'L\(\s*"([^"]*)"', raw)
    if l_m:
        raw = l_m.group(1)
    # C++ MACRO constants used as defaults (INITIAL_LAYER_HEIGHT, etc.) — not real values
    if re.match(r'^[A-Z][A-Z_]+$', raw):
        return None
    # Strip C++ braces from struct/array initializers: "{ ntUndefine }" → "ntUndefine"
    raw = re.sub(r'^\{\s*', '', raw)
    raw = re.sub(r'\s*\}$', '', raw)
    raw = raw.strip()
    # Strip C++ float literal suffix: "5.0f" → "5.0", "1.f" → "1."
    raw = re.sub(r'(\d)f$', r'\1', raw)
    # Normalize trailing dot: "0." → "0", "10." → "10"
    raw = re.sub(r'(\d)\.$', r'\1', raw)
    return raw if raw else None


def extract_default_value(line: str) -> Optional[str]:
    """Extract default value from set_default_value line."""
    # ConfigOptionFloat{0.5}
    m = re.search(r'ConfigOption\w+\s*\{\s*([^}]+)\s*\}', line)
    if m:
        val = m.group(1).strip().strip('"')
        return _clean_default_value(val)
    # ConfigOptionBool(false)
    m = re.search(r'ConfigOption\w+\s*\(\s*([^)]+)\s*\)', line)
    if m:
        return _clean_default_value(m.group(1).strip())
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


def _expand_printconfig_axis_loops(content: str) -> str:
    """Expand the AxisDefault for-loop in PrintConfig.cpp so each axis gets a static def block.

    OrcaSlicer defines machine_max_speed_x/y/z/e (and acceleration/jerk variants) inside:
        for (const AxisDefault &axis : axes) {
            def = this->add("machine_max_speed_" + axis.name, coFloats);
            def->full_label = (boost::format("Maximum speed %1%") % axis_upper).str();
            ...
        }
    The static regex-based parser cannot match the concatenated key, so we expand the loop
    into four copies with literal strings substituted for each axis value.
    """
    axes = [('x', 'X'), ('y', 'Y'), ('z', 'Z'), ('e', 'E')]
    # OrcaSlicer AxisDefault struct values from PrintConfig.cpp (vectors for multi-extruder):
    #   {"x", {500.,200.}, {1000.,1000.}, {10.,10.}}
    #   {"y", {500.,200.}, {1000.,1000.}, {10.,10.}}
    #   {"z", {12.,12.},   {500.,200.},   {0.2,0.4}}
    #   {"e", {120.,120.}, {5000.,5000.}, {2.5,2.5}}
    axis_defaults = {
        'x': {'max_feedrate': '500., 200.', 'max_acceleration': '1000., 1000.', 'max_jerk': '10., 10.'},
        'y': {'max_feedrate': '500., 200.', 'max_acceleration': '1000., 1000.', 'max_jerk': '10., 10.'},
        'z': {'max_feedrate': '12., 12.', 'max_acceleration': '500., 200.', 'max_jerk': '0.2, 0.4'},
        'e': {'max_feedrate': '120., 120.', 'max_acceleration': '5000., 5000.', 'max_jerk': '2.5, 2.5'},
    }
    lines = content.split('\n')
    result: list[str] = []
    i = 0
    while i < len(lines):
        # Detect: for (const AxisDefault &axis : axes)
        if re.search(r'for\s*\(\s*const\s+AxisDefault\s*&\s*\w+\s*:\s*axes\s*\)', lines[i]):
            # Find the loop body between braces
            j = i
            while j < len(lines) and '{' not in lines[j]:
                j += 1
            if j >= len(lines):
                result.append(lines[i])
                i += 1
                continue
            j += 1  # skip opening brace line
            body_lines: list[str] = []
            depth = 1
            while j < len(lines) and depth > 0:
                for ch in lines[j]:
                    if ch == '{':
                        depth += 1
                    elif ch == '}':
                        depth -= 1
                if depth > 0:
                    body_lines.append(lines[j])
                j += 1
            # Expand: for each axis, substitute axis.name and axis_upper
            for lower, upper in axes:
                for body_line in body_lines:
                    expanded = body_line
                    # "prefix_" + axis.name → "prefix_x"
                    expanded = re.sub(
                        r'"([^"]*?)"\s*\+\s*axis\.name',
                        lambda mm: f'"{mm.group(1)}{lower}"',
                        expanded,
                    )
                    # (boost::format("...%1%") % axis_upper).str() → "literal string"
                    fmt_m = re.search(
                        r'\(\s*boost::format\(\s*"([^"]+)"\s*\)\s*%\s*axis_upper\s*\)\.str\(\)',
                        expanded,
                    )
                    if fmt_m:
                        formatted = fmt_m.group(1).replace('%1%', upper)
                        expanded = expanded[:fmt_m.start()] + f'"{formatted}"' + expanded[fmt_m.end():]
                    # axis.max_feedrate / axis.max_acceleration / axis.max_jerk → numeric value
                    for field, val in axis_defaults[lower].items():
                        expanded = re.sub(rf'axis\.{field}', val, expanded)
                    result.append(expanded)
            i = j
            continue
        result.append(lines[i])
        i += 1
    return '\n'.join(result)


def parse_print_config(filepath: str) -> dict:
    """Parse PrintConfig.cpp and extract all setting definitions."""
    with open(filepath, 'r', encoding='utf-8') as f:
        content = strip_block_comments(f.read())

    # Expand the AxisDefault for-loop so axis-specific keys become static definitions
    content = _expand_printconfig_axis_loops(content)

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

        # Extract label (try L("..."), then bare "...", then full_label as fallback)
        m = re.search(r'def->label\s*=\s*L\(\s*"((?:[^"\\]|\\.)*)"\s*\)', block_text)
        if not m:
            m = re.search(r'def->label\s*=\s*"((?:[^"\\]|\\.)*)"\s*;', block_text)
        if m:
            entry['label'] = m.group(1).replace('\\"', '"')
        else:
            # Fall back to full_label if no label was found
            m_full = re.search(r'def->full_label\s*=\s*L\(\s*"((?:[^"\\]|\\.)*)"\s*\)', block_text)
            if not m_full:
                # After axis-loop expansion, full_label is a plain string (not wrapped in L())
                m_full = re.search(r'def->full_label\s*=\s*"((?:[^"\\]|\\.)*)"\s*;', block_text)
            if m_full:
                entry['label'] = m_full.group(1).replace('\\"', '"')

        # Extract tooltip (may span multiple lines with string concatenation)
        m = re.search(r'def->tooltip\s*=\s*L\(\s*"((?:[^"\\]|\\.)*(?:"\s*"(?:[^"\\]|\\.)*)*)"\s*\)', block_text, re.DOTALL)
        if not m:
            # After axis-loop expansion, tooltip is a plain string from boost::format
            m = re.search(r'def->tooltip\s*=\s*"((?:[^"\\]|\\.)*)"\s*;', block_text)
        if m:
            tooltip = m.group(1).replace('\\"', '"').replace('"\n"', '').replace('"  "', '').replace('\n', ' ')
            # Clean up C++ string concatenation artifacts
            tooltip = re.sub(r'"\s+"', '', tooltip)
            entry['tooltip'] = tooltip.strip()

        # Extract sidetext (unit)
        # OrcaSlicer uses several patterns:
        #   L("mm/s")         — translatable ASCII unit
        #   L(u8"mm/s²")     — translatable UTF-8 unit
        #   L(u8"\u2103" /* °C */) — translatable UTF-8 with inline C++ comment
        #   u8"°"             — non-translatable UTF-8 unit
        #   "mm"              — plain ASCII unit
        sidetext_patterns = [
            # L(u8"..." /* comment */) or L(u8"...")
            r'def->sidetext\s*=\s*L\(\s*u8"((?:[^"\\]|\\.)*)"\s*(?:/\*[^*]*\*/\s*)?\)',
            # L("...")
            r'def->sidetext\s*=\s*L\(\s*"((?:[^"\\]|\\.)*)"\s*\)',
            # u8"...";
            r'def->sidetext\s*=\s*u8"((?:[^"\\]|\\.)*)"\s*;',
            # "...";
            r'def->sidetext\s*=\s*"((?:[^"\\]|\\.)*)"\s*;',
        ]
        for pat in sidetext_patterns:
            m = re.search(pat, block_text)
            if m:
                unit = m.group(1)
                # Decode C++ unicode escapes (e.g., \u2103 → ℃)
                unit = unit.encode('utf-8').decode('unicode_escape') if '\\u' in unit else unit
                entry['unit'] = unit
                break

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
            if re.search(rf'def->mode\s*=\s*{mode_cpp}', block_text):
                entry['mode'] = mode_str
                break

        # Extract category
        m = re.search(r'def->category\s*=\s*L\(\s*"((?:[^"\\]|\\.)*)"\s*\)', block_text)
        if m:
            entry['category'] = m.group(1)

        # Extract default value
        m = re.search(r'set_default_value\(new\s+ConfigOption\w+\s*[{(]\s*([^})]+)\s*[})]', block_text)
        if m:
            raw_default = m.group(1).strip().strip('"')
            # Clean Vec2d(x, y) → "x, y"
            vec_match = re.match(r'Vec2d\(\s*(.+)', raw_default)
            if vec_match:
                raw_default = vec_match.group(1).rstrip(')')
            cleaned = _clean_default_value(raw_default)
            if cleaned is not None:
                entry['default'] = cleaned

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

    # Labels defined in Tab.cpp rather than PrintConfig.cpp
    LABEL_OVERRIDES = {
        'fan_speedup_time': 'Fan speed-up time',
    }
    for key, label in LABEL_OVERRIDES.items():
        if key in settings:
            settings[key]['label'] = label

    return settings


# ── Tab.cpp parser ─────────────────────────────────────────────────────────


def _expand_vector_loops(body: str) -> str:
    """Expand C++ for-range loops over inline string vectors.

    Handles patterns like:
        const std::vector<std::string> axes{ "x", "y", "z", "e" };
        for (const std::string &axis : axes) {
            append_option_line(optgroup, "machine_max_acceleration_" + axis, "...");
        }
    Produces expanded lines with the concatenated key for each value.
    """
    # Step 1: collect inline vector declarations
    vec_pattern = re.compile(
        r'(?:const\s+)?std::vector<std::string>\s+(\w+)\s*\{([^}]+)\}',
    )
    vectors: dict[str, list[str]] = {}
    for m in vec_pattern.finditer(body):
        var_name = m.group(1)
        values = re.findall(r'"([^"]*)"', m.group(2))
        vectors[var_name] = values

    if not vectors:
        return body

    # Step 2: find for-range loops over known vectors and expand them
    result_lines: list[str] = []
    lines = body.split('\n')
    i = 0
    while i < len(lines):
        # Match: for (const std::string &var : vec_name)
        loop_match = re.search(
            r'for\s*\(\s*(?:const\s+)?(?:auto|std::string)\s*[&]?\s*(\w+)\s*:\s*(\w+)\s*\)',
            lines[i],
        )
        if loop_match:
            loop_var = loop_match.group(1)
            vec_name = loop_match.group(2)
            if vec_name in vectors:
                # Find the loop body (between braces)
                loop_body_lines: list[str] = []
                # Skip to opening brace
                j = i
                found_open = False
                while j < len(lines):
                    if '{' in lines[j]:
                        found_open = True
                        j += 1
                        break
                    j += 1
                if not found_open:
                    result_lines.append(lines[i])
                    i += 1
                    continue
                # Collect until closing brace
                depth = 1
                while j < len(lines) and depth > 0:
                    for ch in lines[j]:
                        if ch == '{':
                            depth += 1
                        elif ch == '}':
                            depth -= 1
                    if depth > 0:
                        loop_body_lines.append(lines[j])
                    j += 1
                # Expand: for each vector value, substitute the variable
                for val in vectors[vec_name]:
                    for body_line in loop_body_lines:
                        # Handle "prefix_" + var concatenation
                        expanded = re.sub(
                            rf'"([^"]*?)"\s*\+\s*{re.escape(loop_var)}',
                            lambda mm: f'"{mm.group(1)}{val}"',
                            body_line,
                        )
                        # Handle bare variable reference (e.g., append_option_line(og, var, ...))
                        expanded = re.sub(
                            rf'\b{re.escape(loop_var)}\b',
                            f'"{val}"',
                            expanded,
                        )
                        result_lines.append(expanded)
                i = j
                continue
        result_lines.append(lines[i])
        i += 1
    return '\n'.join(result_lines)


def parse_tab_layout(filepath: str, method_name: str = 'TabFilament::build') -> list:
    """Parse Tab.cpp to extract the tab/section/field layout for a specific tab class."""
    with open(filepath, 'r', encoding='utf-8') as f:
        content = strip_block_comments(f.read())

    # Find the method — support any return type (void, PageShp, etc.)
    m = re.search(rf'^\w[\w:*&<> ]*\s+{re.escape(method_name)}\s*\(', content, re.MULTILINE)
    if not m:
        print(f"Warning: {method_name} not found in {filepath}", file=sys.stderr)
        return []
    start = m.start()

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

    # Pre-process: expand C++ for-range loops over inline string vectors
    method_body = _expand_vector_loops(method_body)

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

        # Dynamic tab page: add_options_page(wxString::Format("Extruder %d", ...), "icon")
        # or other non-L() patterns for dynamically named pages (e.g. per-extruder)
        m = re.search(r'add_options_page\(\s*(?:wxString::Format\(\s*"([^"]+)"'
                       r'|from_u8\(\s*"([^"]+)"'
                       r'|"([^"]+)")'
                       r'.*?,\s*"([^"]*)"', stripped)
        if m:
            raw_name = m.group(1) or m.group(2) or m.group(3) or 'Unknown'
            # Normalize dynamic format strings: "Extruder %d" -> "Extruder"
            tab_name = re.sub(r'\s*%[ds].*', '', raw_name).strip()
            current_tab = {
                'name': tab_name,
                'icon': m.group(4),
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

        # append_option_line(optgroup, "field_name", ...) — used in kinematics page
        m = re.search(r'append_option_line\(\s*\w+\s*,\s*"([^"]+)"', stripped)
        if m and current_section:
            current_section['fields'].append({
                'key': m.group(1),
                'compound': False,
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


def parse_process_tabs(filepath: str) -> list:
    """Parse process (print) tab layout from TabPrint::build."""
    return parse_tab_layout(filepath, 'TabPrint::build')


def parse_machine_tabs(filepath: str) -> list:
    """Parse machine tab layout from multiple TabPrinter methods."""
    # Main tabs: Basic information, Machine G-code, Notes
    tabs = parse_tab_layout(filepath, 'TabPrinter::build_fff')
    # Multimaterial + Extruder pages — built via build_unregular_pages.
    # Note: OrcaSlicer creates the Extruder page with a dynamic name
    # (wxString::Format("Extruder %d", i+1)) rather than L("..."),
    # inside an index-based for loop, which parse_tab_layout cannot extract.
    # So we manually construct the Extruder tab based on the known structure.
    unregular = parse_tab_layout(filepath, 'TabPrinter::build_unregular_pages')
    if unregular:
        # Multimaterial tab is parsed correctly, but Extruder tab is not
        # The Extruder tab has sections that are currently under Multimaterial
        # We need to manually create it and move those sections
        multimaterial_tab = None
        for tab in unregular:
            if tab['name'] == 'Multimaterial':
                multimaterial_tab = tab
                break
        
        if multimaterial_tab:
            # Create Extruder tab
            extruder_tab = {
                'name': 'Extruder',
                'icon': 'custom-gcode_extruder',
                'sections': []
            }
            
            # Sections that belong in Extruder (from Tab.cpp line ~4940):
            # Basic information, Layer height limits, Position, Retraction, Z-Hop, 
            # Retraction when switching material
            extruder_section_names = {
                'Basic information', 'Layer height limits', 'Position',
                'Retraction', 'Z-Hop', 'Retraction when switching material'
            }
            
            # Move sections from Multimaterial to Extruder
            remaining_mm_sections = []
            for section in multimaterial_tab['sections']:
                if section['name'] in extruder_section_names:
                    extruder_tab['sections'].append(section)
                else:
                    remaining_mm_sections.append(section)
            
            multimaterial_tab['sections'] = remaining_mm_sections
            
            # Insert Extruder tab after Multimaterial
            mm_idx = unregular.index(multimaterial_tab)
            unregular.insert(mm_idx + 1, extruder_tab)
        
        tabs.extend(unregular)
    
    # Motion ability (kinematics) — built via separate method
    kinematics = parse_tab_layout(filepath, 'TabPrinter::build_kinematics_page')
    if kinematics:
        tabs.extend(kinematics)
    
    # Reorder tabs to match OrcaSlicer UI order:
    # Basic information, Machine G-code, Multimaterial, Extruder, Motion ability, Notes
    tab_order = ['Basic information', 'Machine G-code', 'Multimaterial', 'Extruder', 'Motion ability', 'Notes']
    tabs_by_name = {tab['name']: tab for tab in tabs}
    ordered_tabs = []
    for name in tab_order:
        if name in tabs_by_name:
            ordered_tabs.append(tabs_by_name[name])
    
    # Add any remaining tabs not in the predefined order
    for tab in tabs:
        if tab not in ordered_tabs:
            ordered_tabs.append(tab)
    
    return ordered_tabs


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

    # Parse Tab.cpp for tab layouts
    if os.path.exists(tab_path):
        print(f"Parsing {tab_path} for tab layouts...", file=sys.stderr)
        filament_tabs = parse_filament_tabs(tab_path)
        machine_tabs = parse_machine_tabs(tab_path)
        process_tabs = parse_process_tabs(tab_path)
        print(f"  Found {len(filament_tabs)} filament tabs, {len(machine_tabs)} machine tabs, {len(process_tabs)} process tabs", file=sys.stderr)
    else:
        filament_tabs = []
        machine_tabs = []
        process_tabs = []

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

    # Build process-related settings
    process_keys = set()
    for tab in process_tabs:
        for section in tab['sections']:
            for field in section['fields']:
                process_keys.add(field['key'])
    # Also include known process-prefixed settings not in tabs
    for key in all_settings:
        if key.startswith('support_') or key.startswith('tree_support_') or key.startswith('brim_') or key.startswith('skirt_'):
            process_keys.add(key)

    process_metadata = {}
    for key in sorted(process_keys):
        if key in all_settings:
            process_metadata[key] = all_settings[key]
        else:
            process_metadata[key] = {'key': key, 'label': key, 'type': 'unknown'}

    result = {
        '_meta': {
            'source': 'OrcaSlicer PrintConfig.cpp + Tab.cpp',
            'generator': 'tools/extract-orca-metadata.py',
            'totalSettings': len(all_settings),
            'filamentSettings': len(filament_metadata),
            'machineSettings': len(machine_metadata),
            'processSettings': len(process_metadata),
        },
        'filament': {
            'tabs': filament_tabs,
            'settings': filament_metadata,
        },
        'machine': {
            'tabs': machine_tabs,
            'settings': machine_metadata,
        },
        'process': {
            'tabs': process_tabs,
            'settings': process_metadata,
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
