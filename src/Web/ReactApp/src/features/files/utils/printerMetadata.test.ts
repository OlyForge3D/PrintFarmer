import { describe, expect, it } from 'vitest';
import { printerMetadataRows } from '@/features/files/utils/printerMetadata';

const labels = (rows: Array<{ label: string }>) => rows.map((row) => row.label);

describe('printerMetadataRows', () => {
  it('omits per-tool values that only repeat the single-value field', () => {
    const rows = printerMetadataRows({
      nozzleTemperatureC: 215, nozzleTemperaturePerToolC: [215],
      filamentUsedMm: 800, filamentUsedPerToolMm: [800],
      bedTemperatureC: 60, bedTemperaturePerToolC: [60, 65],
    });
    expect(labels(rows)).toEqual(['Bed temperature per tool']);
  });

  it('omits per-tool material and mass only when tool cards show them', () => {
    const perTool = { filamentTypePerTool: ['PLA', 'PETG'], filamentUsedPerToolG: [3, 1] };
    expect(labels(printerMetadataRows(perTool))).toEqual(['Filament type per tool', 'Filament mass per tool']);
    expect(printerMetadataRows({ ...perTool, tools: [{ index: 0 }, { index: 1 }] })).toEqual([]);
  });

  it('never repeats the print time or layer height shown in the detail rows', () => {
    expect(printerMetadataRows({ printTimeSeconds: 5400, layerHeightMm: 0.2, estimatedPrintTimeSeconds: 5400 })).toEqual([]);
    expect(printerMetadataRows({ estimatedPrintTimeSeconds: 5460 })).toEqual([]);
    expect(printerMetadataRows({ printTimeSeconds: 5400, estimatedPrintTimeSeconds: 5460 }))
      .toEqual([{ label: 'Estimated print time', value: '5460 s' }]);
  });
});
