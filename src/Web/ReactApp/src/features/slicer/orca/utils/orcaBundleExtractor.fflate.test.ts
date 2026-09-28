// Exercises the real fflate library; the sibling suite mocks it.
import { describe, it, expect } from 'vitest';
import { zipSync, strToU8 } from 'fflate';
import { extractOrcaBundle, isZipFile } from './orcaBundleExtractor';

/**
 * Builds a ZIP64 archive whose central directory entry declares
 * compressed_size = 0xFFFFFFFF but has no ZIP64 extra field (tag 0x0001).
 * fflate <= 0.8.2 loops forever on this input (GHSA-px8p-9vwx-vf98).
 */
function craftZip64WithoutExtraField(): Uint8Array {
  const name = strToU8('a.json');
  const localHeaderSize = 30 + name.length;
  const centralSize = 46 + name.length;
  const centralOffset = localHeaderSize;
  const zip64EocdOffset = centralOffset + centralSize;
  const total = zip64EocdOffset + 56 + 20 + 22;

  const buf = new Uint8Array(total);
  const view = new DataView(buf.buffer);
  const u16 = (off: number, v: number) => view.setUint16(off, v, true);
  const u32 = (off: number, v: number) => view.setUint32(off, v, true);
  const u64 = (off: number, v: number) => view.setBigUint64(off, BigInt(v), true);

  // Local file header (stored, empty payload)
  u32(0, 0x04034b50);
  u16(4, 45);
  u16(26, name.length);
  buf.set(name, 30);

  // Central directory header with ZIP64 size sentinel and no extra field
  let p = centralOffset;
  u32(p, 0x02014b50);
  u16(p + 4, 45);
  u16(p + 6, 45);
  u32(p + 20, 0xffffffff);
  u16(p + 28, name.length);
  u16(p + 30, 0);
  u32(p + 42, 0);
  buf.set(name, p + 46);

  // ZIP64 end of central directory record
  p = zip64EocdOffset;
  u32(p, 0x06064b50);
  u64(p + 4, 44);
  u16(p + 12, 45);
  u16(p + 14, 45);
  u64(p + 24, 1);
  u64(p + 32, 1);
  u64(p + 40, centralSize);
  u64(p + 48, centralOffset);

  // ZIP64 end of central directory locator
  p += 56;
  u32(p, 0x07064b50);
  u64(p + 8, zip64EocdOffset);
  u32(p + 16, 1);

  // End of central directory record
  p += 20;
  u32(p, 0x06054b50);
  u16(p + 8, 1);
  u16(p + 10, 1);
  u32(p + 12, centralSize);
  u32(p + 16, 0xffffffff); // defer the CD offset to the ZIP64 record

  return buf;
}

describe('orcaBundleExtractor with real fflate', () => {
  it('extracts presets from a genuine ZIP bundle', async () => {
    const zip = zipSync({
      'printer/Test Printer.json': strToU8(JSON.stringify({ printer_settings_id: 'Test Printer' })),
      'filament/Generic PLA.json': strToU8(JSON.stringify({ filament_settings_id: ['Generic PLA'] })),
      'process/0.20mm.json': strToU8(JSON.stringify({ print_settings_id: '0.20mm' })),
      'bundle_structure.json': strToU8('{}'),
    });

    expect(isZipFile(zip)).toBe(true);
    const result = JSON.parse(await extractOrcaBundle(zip));
    expect(result.printer).toEqual([{ printer_settings_id: 'Test Printer' }]);
    expect(result.filament).toEqual([{ filament_settings_id: ['Generic PLA'] }]);
    expect(result.process).toEqual([{ print_settings_id: '0.20mm' }]);
  });

  it('rejects a ZIP64 entry missing its extra field instead of hanging', async () => {
    const zip = craftZip64WithoutExtraField();

    expect(isZipFile(zip)).toBe(true);
    await expect(extractOrcaBundle(zip)).rejects.toThrow(/Failed to extract bundle/);
  });
});
