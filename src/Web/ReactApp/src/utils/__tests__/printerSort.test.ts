import { describe, it, expect } from 'vitest';
import { sortPrintersByAvailability } from '@/utils/printerSort';

describe('sortPrintersByAvailability', () => {
  it('returns empty array for empty input', () => {
    expect(sortPrintersByAvailability([])).toEqual([]);
  });

  it('sorts online printers before offline printers', () => {
    const printers = [
      { name: 'Alpha', isOnline: false },
      { name: 'Bravo', isOnline: true },
    ];
    const sorted = sortPrintersByAvailability(printers);
    expect(sorted.map(p => p.name)).toEqual(['Bravo', 'Alpha']);
  });

  it('sorts alphabetically within the same online state', () => {
    const printers = [
      { name: 'Charlie', isOnline: true },
      { name: 'Alpha', isOnline: true },
      { name: 'Bravo', isOnline: true },
    ];
    const sorted = sortPrintersByAvailability(printers);
    expect(sorted.map(p => p.name)).toEqual(['Alpha', 'Bravo', 'Charlie']);
  });

  it('groups online first then offline, each group alphabetical', () => {
    const printers = [
      { name: 'Zulu', isOnline: false },
      { name: 'Alpha', isOnline: true },
      { name: 'Mike', isOnline: false },
      { name: 'Delta', isOnline: true },
      { name: 'Echo', isOnline: false },
    ];
    const sorted = sortPrintersByAvailability(printers);
    expect(sorted.map(p => p.name)).toEqual([
      'Alpha', 'Delta',          // online, alphabetical
      'Echo', 'Mike', 'Zulu',    // offline, alphabetical
    ]);
  });

  it('treats undefined isOnline as offline', () => {
    const printers = [
      { name: 'Online', isOnline: true },
      { name: 'Unknown', isOnline: undefined },
      { name: 'Offline', isOnline: false },
    ];
    const sorted = sortPrintersByAvailability(printers);
    expect(sorted[0].name).toBe('Online');
    expect(sorted[1].isOnline).toBeFalsy();
    expect(sorted[2].isOnline).toBeFalsy();
  });

  it('treats undefined name as empty string for comparison', () => {
    const printers = [
      { name: undefined, isOnline: true },
      { name: 'Alpha', isOnline: true },
    ];
    const sorted = sortPrintersByAvailability(printers);
    expect(sorted.map(p => p.name)).toEqual([undefined, 'Alpha']);
  });

  it('does not mutate the original array', () => {
    const printers = [
      { name: 'Bravo', isOnline: false },
      { name: 'Alpha', isOnline: true },
    ];
    const original = [...printers];
    sortPrintersByAvailability(printers);
    expect(printers).toEqual(original);
  });

  it('handles single printer', () => {
    const printers = [{ name: 'Solo', isOnline: true }];
    const sorted = sortPrintersByAvailability(printers);
    expect(sorted).toEqual([{ name: 'Solo', isOnline: true }]);
  });

  it('preserves extra properties on printer objects', () => {
    const printers = [
      { id: '1', name: 'Bravo', isOnline: false, model: 'X1' },
      { id: '2', name: 'Alpha', isOnline: true, model: 'P1' },
    ];
    const sorted = sortPrintersByAvailability(printers);
    expect(sorted[0]).toEqual({ id: '2', name: 'Alpha', isOnline: true, model: 'P1' });
    expect(sorted[1]).toEqual({ id: '1', name: 'Bravo', isOnline: false, model: 'X1' });
  });
});
