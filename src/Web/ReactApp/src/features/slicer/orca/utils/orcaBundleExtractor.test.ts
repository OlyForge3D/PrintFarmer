import { describe, it, expect, vi, beforeEach } from 'vitest';

// Mock fflate - jsdom's Uint8Array breaks fflate's instanceof checks
vi.mock('fflate', () => ({
  unzipSync: vi.fn(),
}));

import { unzipSync } from 'fflate';
import { isZipFile, extractOrcaBundle } from './orcaBundleExtractor';

const mockedUnzip = vi.mocked(unzipSync);

/** Helper: encode a JS object as a Uint8Array (UTF-8 JSON) */
function toU8(obj: unknown): Uint8Array {
  return new TextEncoder().encode(JSON.stringify(obj));
}

describe('orcaBundleExtractor', () => {
  // ── isZipFile ─────────────────────────────────────────────
  describe('isZipFile', () => {
    it('returns true for ZIP magic bytes', () => {
      expect(isZipFile(new Uint8Array([0x50, 0x4b, 0x03, 0x04, 0x00]))).toBe(true);
    });

    it('returns false for JSON starting with {', () => {
      expect(isZipFile(new TextEncoder().encode('{"key":"val"}'))).toBe(false);
    });

    it('returns false for JSON starting with [', () => {
      expect(isZipFile(new TextEncoder().encode('[1,2]'))).toBe(false);
    });

    it('returns false for empty buffer', () => {
      expect(isZipFile(new Uint8Array([]))).toBe(false);
    });

    it('returns false for buffer shorter than 4 bytes', () => {
      expect(isZipFile(new Uint8Array([0x50, 0x4b]))).toBe(false);
    });

    it('accepts ArrayBuffer as well as Uint8Array', () => {
      const buf = new Uint8Array([0x50, 0x4b, 0x03, 0x04]).buffer;
      expect(isZipFile(buf)).toBe(true);
    });
  });

  // ── extractOrcaBundle ─────────────────────────────────────
  describe('extractOrcaBundle', () => {
    const printerPreset = {
      printer_settings_id: 'Test Printer',
      name: 'Test Printer',
      nozzle_diameter: ['0.4'],
      printer_model: 'Generic',
    };
    const filamentPreset = {
      filament_settings_id: ['Generic PLA'],
      name: 'Generic PLA',
      filament_type: 'PLA',
    };
    const processPreset = {
      print_settings_id: 'Standard Quality',
      name: 'Standard Quality',
      layer_height: '0.2',
    };

    beforeEach(() => {
      vi.clearAllMocks();
    });

    it('extracts and returns combined JSON with all three categories', async () => {
      mockedUnzip.mockReturnValue({
        'printer/Printer.json': toU8(printerPreset),
        'filament/Filament.json': toU8(filamentPreset),
        'process/Process.json': toU8(processPreset),
        'bundle_structure.json': toU8({ bundle_type: 'printer config bundle' }),
      } as Record<string, Uint8Array>);

      const result = await extractOrcaBundle(new Uint8Array(8));
      const parsed = JSON.parse(result);

      expect(parsed).toHaveProperty('printer');
      expect(parsed).toHaveProperty('filament');
      expect(parsed).toHaveProperty('process');
    });

    it('categorizes printer presets by printer_settings_id', async () => {
      mockedUnzip.mockReturnValue({
        'printer/P.json': toU8(printerPreset),
      } as Record<string, Uint8Array>);

      const parsed = JSON.parse(await extractOrcaBundle(new Uint8Array(8)));
      expect(parsed.printer).toHaveLength(1);
      expect(parsed.printer[0].printer_settings_id).toBe('Test Printer');
      expect(parsed.printer[0].nozzle_diameter).toEqual(['0.4']);
    });

    it('categorizes filament presets by filament_settings_id', async () => {
      mockedUnzip.mockReturnValue({
        'filament/F.json': toU8(filamentPreset),
      } as Record<string, Uint8Array>);

      const parsed = JSON.parse(await extractOrcaBundle(new Uint8Array(8)));
      expect(parsed.filament).toHaveLength(1);
      expect(parsed.filament[0].filament_type).toBe('PLA');
    });

    it('categorizes process presets by print_settings_id', async () => {
      mockedUnzip.mockReturnValue({
        'process/P.json': toU8(processPreset),
      } as Record<string, Uint8Array>);

      const parsed = JSON.parse(await extractOrcaBundle(new Uint8Array(8)));
      expect(parsed.process).toHaveLength(1);
      expect(parsed.process[0].print_settings_id).toBe('Standard Quality');
      expect(parsed.process[0].layer_height).toBe('0.2');
    });

    it('handles UTF-8 content with special characters', async () => {
      const utf8Preset = {
        printer_settings_id: 'Test 测试 🖨️',
        name: 'Prüfer Imprimante',
        nozzle_diameter: ['0.4'],
      };
      mockedUnzip.mockReturnValue({
        'printer/utf8.json': toU8(utf8Preset),
      } as Record<string, Uint8Array>);

      const parsed = JSON.parse(await extractOrcaBundle(new Uint8Array(8)));
      expect(parsed.printer[0].printer_settings_id).toBe('Test 测试 🖨️');
      expect(parsed.printer[0].name).toBe('Prüfer Imprimante');
    });

    it('skips bundle_structure.json (manifest, not a preset)', async () => {
      mockedUnzip.mockReturnValue({
        'bundle_structure.json': toU8({ bundle_type: 'printer config bundle', printer_config: [] }),
        'printer/P.json': toU8(printerPreset),
      } as Record<string, Uint8Array>);

      const parsed = JSON.parse(await extractOrcaBundle(new Uint8Array(8)));
      expect(parsed.printer).toHaveLength(1);
      // bundle_structure should not appear in any category
      expect(parsed.filament).toHaveLength(0);
      expect(parsed.process).toHaveLength(0);
    });

    it('skips non-JSON files', async () => {
      mockedUnzip.mockReturnValue({
        'printer/P.json': toU8(printerPreset),
        'README.txt': new TextEncoder().encode('readme'),
        'image.png': new Uint8Array([0x89, 0x50, 0x4e, 0x47]),
      } as Record<string, Uint8Array>);

      const parsed = JSON.parse(await extractOrcaBundle(new Uint8Array(8)));
      expect(parsed.printer).toHaveLength(1);
    });

    it('skips presets with no discriminator field', async () => {
      mockedUnzip.mockReturnValue({
        'unknown/Mystery.json': toU8({ name: 'Unknown', some_field: 'value' }),
        'printer/P.json': toU8(printerPreset),
      } as Record<string, Uint8Array>);

      const parsed = JSON.parse(await extractOrcaBundle(new Uint8Array(8)));
      expect(parsed.printer).toHaveLength(1);
      expect(parsed.filament).toHaveLength(0);
      expect(parsed.process).toHaveLength(0);
    });

    it('handles multiple presets of the same type', async () => {
      mockedUnzip.mockReturnValue({
        'printer/A.json': toU8({ ...printerPreset, printer_settings_id: 'Printer A' }),
        'printer/B.json': toU8({ ...printerPreset, printer_settings_id: 'Printer B' }),
        'printer/C.json': toU8({ ...printerPreset, printer_settings_id: 'Printer C' }),
      } as Record<string, Uint8Array>);

      const parsed = JSON.parse(await extractOrcaBundle(new Uint8Array(8)));
      expect(parsed.printer).toHaveLength(3);
      const ids = parsed.printer.map((p: Record<string, unknown>) => p.printer_settings_id);
      expect(ids).toContain('Printer A');
      expect(ids).toContain('Printer B');
      expect(ids).toContain('Printer C');
    });

    it('handles deeply nested directory structures', async () => {
      mockedUnzip.mockReturnValue({
        'configs/printers/sub/Test.json': toU8(printerPreset),
        'configs/filaments/Generic PLA.json': toU8(filamentPreset),
      } as Record<string, Uint8Array>);

      const parsed = JSON.parse(await extractOrcaBundle(new Uint8Array(8)));
      expect(parsed.printer).toHaveLength(1);
      expect(parsed.filament).toHaveLength(1);
    });

    it('returns empty arrays when ZIP has no JSON files', async () => {
      mockedUnzip.mockReturnValue({
        'README.txt': new TextEncoder().encode('hello'),
      } as Record<string, Uint8Array>);

      const parsed = JSON.parse(await extractOrcaBundle(new Uint8Array(8)));
      expect(parsed.printer).toEqual([]);
      expect(parsed.filament).toEqual([]);
      expect(parsed.process).toEqual([]);
    });

    it('throws on corrupt ZIP data', async () => {
      mockedUnzip.mockImplementation(() => { throw new Error('invalid zip data'); });

      await expect(extractOrcaBundle(new Uint8Array(8))).rejects.toThrow('Failed to extract bundle');
    });

    it('skips individual files with invalid JSON gracefully', async () => {
      mockedUnzip.mockReturnValue({
        'printer/bad.json': new TextEncoder().encode('{ not valid json }'),
        'printer/good.json': toU8(printerPreset),
      } as Record<string, Uint8Array>);

      const parsed = JSON.parse(await extractOrcaBundle(new Uint8Array(8)));
      // Good preset extracted, bad one skipped
      expect(parsed.printer).toHaveLength(1);
      expect(parsed.printer[0].printer_settings_id).toBe('Test Printer');
    });
  });

  // ── Integration: file type detection ──────────────────────
  describe('file type detection integration', () => {
    it('JSON text is not detected as ZIP', () => {
      const json = new TextEncoder().encode('{"printer":[]}');
      expect(isZipFile(json)).toBe(false);
    });

    it('ZIP magic bytes are detected correctly', () => {
      // Real ZIP header
      const zip = new Uint8Array([0x50, 0x4b, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00]);
      expect(isZipFile(zip)).toBe(true);
    });

    it('bundleJson output has correct shape for preview API', async () => {
      mockedUnzip.mockReturnValue({
        'printer/P.json': toU8({ printer_settings_id: 'X', name: 'X' }),
      } as Record<string, Uint8Array>);

      const bundleJson = await extractOrcaBundle(new Uint8Array(8));
      const parsed = JSON.parse(bundleJson);

      expect(Array.isArray(parsed.printer)).toBe(true);
      expect(Array.isArray(parsed.filament)).toBe(true);
      expect(Array.isArray(parsed.process)).toBe(true);
    });
  });
});
