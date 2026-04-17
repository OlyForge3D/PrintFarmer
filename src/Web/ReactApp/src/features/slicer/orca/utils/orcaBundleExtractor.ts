import { unzipSync } from 'fflate';

/**
 * Check if the provided data is a ZIP file by inspecting magic bytes.
 * ZIP files start with "PK\x03\x04" (0x50 0x4B 0x03 0x04).
 */
export function isZipFile(data: ArrayBuffer | Uint8Array): boolean {
  const view = data instanceof Uint8Array ? data : new Uint8Array(data);
  if (view.byteLength < 4) return false;
  return view[0] === 0x50 && view[1] === 0x4b && view[2] === 0x03 && view[3] === 0x04;
}

interface OrcaPreset {
  name?: string;
  printer_settings_id?: string;
  filament_settings_id?: string;
  print_settings_id?: string;
  [key: string]: unknown;
}

interface OrcaBundleStructure {
  printer?: OrcaPreset[];
  filament?: OrcaPreset[];
  process?: OrcaPreset[];
}

/**
 * Extract and parse an OrcaSlicer ZIP bundle (.orca_printer or .orca_filament).
 * Returns a JSON string in the format expected by the backend preview API:
 * { "printer": [...], "filament": [...], "process": [...] }
 */
export async function extractOrcaBundle(data: ArrayBuffer | Uint8Array): Promise<string> {
  try {
    // Extract ZIP contents
    const uint8Data = data instanceof Uint8Array ? data : new Uint8Array(data);
    const unzipped = unzipSync(uint8Data);

    const bundle: OrcaBundleStructure = {
      printer: [],
      filament: [],
      process: [],
    };

    // Process all JSON files in the ZIP
    for (const [filename, fileData] of Object.entries(unzipped)) {
      // Skip non-JSON files and bundle_structure.json (it's metadata, not preset data)
      if (!filename.endsWith('.json') || filename.includes('bundle_structure')) {
        continue;
      }

      try {
        // Decode UTF-8 content
        const decoder = new TextDecoder('utf-8');
        const content = decoder.decode(fileData);
        const preset = JSON.parse(content) as OrcaPreset;

        // Detect preset type from discriminator fields
        if (preset.printer_settings_id !== undefined) {
          bundle.printer!.push(preset);
        } else if (preset.filament_settings_id !== undefined) {
          bundle.filament!.push(preset);
        } else if (preset.print_settings_id !== undefined) {
          bundle.process!.push(preset);
        }
        // If none of these fields exist, skip this file (unknown type)
      } catch (parseError) {
        // If we can't parse a single file, log but continue with others
        console.warn(`Failed to parse ${filename}:`, parseError);
      }
    }

    // Return combined bundle JSON (same format as direct JSON upload)
    return JSON.stringify(bundle);
  } catch (error) {
    throw new Error(`Failed to extract bundle: ${error instanceof Error ? error.message : 'Unknown error'}`, {
      cause: error,
    });
  }
}
