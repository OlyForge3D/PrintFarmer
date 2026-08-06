import { describe, it, expect } from 'vitest';
import { sortPrintersForDisplay } from '../printerDisplaySort';
import { PrinterBackend, type Printer } from '@/types/api';

function makePrinter(overrides: Partial<Printer> = {}): Printer {
  return {
    id: 'printer-id',
    name: 'Printer',
    backend: PrinterBackend.Moonraker,
    isOnline: true,
    isEnabled: true,
    state: 'Idle',
    ...overrides,
  } as Printer;
}

describe('printerDisplaySort (#1146 item 5)', () => {
  it('does not mutate the input array (copy-before-sort)', () => {
    const alpha = makePrinter({ id: 'p-1', name: 'Bravo', state: 'Idle' });
    const beta = makePrinter({ id: 'p-2', name: 'Alpha', state: 'Idle' });
    const input = [alpha, beta];
    const inputCopy = [...input];

    const result = sortPrintersForDisplay(input, 'name', new Set());

    // The input array's element order is untouched...
    expect(input).toEqual(inputCopy);
    expect(input[0]).toBe(alpha);
    expect(input[1]).toBe(beta);
    // ...even though the *result* is correctly sorted and is a different array.
    expect(result).not.toBe(input);
    expect(result.map(p => p.name)).toEqual(['Alpha', 'Bravo']);
  });

  it('never mutates the same array reference passed in twice in a row', () => {
    // Regression guard for the upstream-mutation bug: PrintersPage used to
    // call `filtered.sort()` directly on `optimisticPrinters` whenever every
    // filter was a no-op, corrupting the upstream array/query-cache data.
    const shared = [
      makePrinter({ id: 'p-1', name: 'Zulu' }),
      makePrinter({ id: 'p-2', name: 'Alpha' }),
    ];
    const beforeFirstCall = [...shared];

    sortPrintersForDisplay(shared, 'name', new Set());

    expect(shared).toEqual(beforeFirstCall);
  });

  it('sorts by name using default-locale ordering (state mode, tie-break by name)', () => {
    const printers = [
      makePrinter({ id: 'p-1', name: 'Charlie', state: 'Idle' }),
      makePrinter({ id: 'p-2', name: 'Alpha', state: 'Idle' }),
      makePrinter({ id: 'p-3', name: 'Bravo', state: 'Idle' }),
    ];

    const result = sortPrintersForDisplay(printers, 'state', new Set());

    expect(result.map(p => p.name)).toEqual(['Alpha', 'Bravo', 'Charlie']);
  });

  it('sorts "state" mode with printing first, then paused, then idle, then offline', () => {
    const printing = makePrinter({ id: 'p-1', name: 'Printing1', state: 'Printing', isOnline: true });
    const paused = makePrinter({ id: 'p-2', name: 'Paused1', state: 'Paused', isOnline: true });
    const idle = makePrinter({ id: 'p-3', name: 'Idle1', state: 'Idle', isOnline: true });
    const offline = makePrinter({ id: 'p-4', name: 'Offline1', state: 'Idle', isOnline: false });

    const result = sortPrintersForDisplay([offline, idle, paused, printing], 'state', new Set());

    expect(result.map(p => p.id)).toEqual(['p-1', 'p-2', 'p-3', 'p-4']);
  });

  it('puts printers in pendingPrinterIds first in "state" mode regardless of their own status', () => {
    const printing = makePrinter({ id: 'p-1', name: 'Printing1', state: 'Printing', isOnline: true });
    const pendingOffline = makePrinter({ id: 'p-2', name: 'PendingButOffline', state: 'Idle', isOnline: false });

    const result = sortPrintersForDisplay(
      [printing, pendingOffline],
      'state',
      new Set(['p-2']),
    );

    expect(result.map(p => p.id)).toEqual(['p-2', 'p-1']);
  });

  it('sorts "backend" mode by backend name, then by printer name within a backend', () => {
    const printers = [
      makePrinter({ id: 'p-1', name: 'Zulu', backend: PrinterBackend.Moonraker }),
      makePrinter({ id: 'p-2', name: 'Alpha', backend: PrinterBackend.PrusaLink }),
      makePrinter({ id: 'p-3', name: 'Alpha', backend: PrinterBackend.Moonraker }),
    ];

    const result = sortPrintersForDisplay(printers, 'backend', new Set());

    expect(result.map(p => p.id)).toEqual(['p-3', 'p-1', 'p-2']);
  });

  it('produces a stable order for exact ties, preserving original relative order', () => {
    const printers = [
      makePrinter({ id: 'p-1', name: 'Same', state: 'Idle' }),
      makePrinter({ id: 'p-2', name: 'Same', state: 'Idle' }),
      makePrinter({ id: 'p-3', name: 'Same', state: 'Idle' }),
    ];

    const result = sortPrintersForDisplay(printers, 'name', new Set());

    expect(result.map(p => p.id)).toEqual(['p-1', 'p-2', 'p-3']);
  });

  it('treats missing printer names as empty strings, matching previous localeCompare behavior', () => {
    const printers = [
      makePrinter({ id: 'p-1', name: undefined as unknown as string }),
      makePrinter({ id: 'p-2', name: 'Alpha' }),
    ];

    const result = sortPrintersForDisplay(printers, 'name', new Set());

    expect(result.map(p => p.id)).toEqual(['p-1', 'p-2']);
  });

  it('returns an empty array for empty input without throwing', () => {
    expect(sortPrintersForDisplay([], 'state', new Set())).toEqual([]);
  });
});