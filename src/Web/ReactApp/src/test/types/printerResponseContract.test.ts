import { describe, expect, it } from 'vitest';
import { MotionType, PrinterBackend, type Printer, type PrinterFast } from '@/types/api';

describe('printer response contract', () => {
  it('accepts a list response without suppressed connection URLs or client-only reachability', () => {
    // CompletePrinterDto: nulls are omitted and BackendUrl is ignored on writes.
    const response = {
      id: '00000000-0000-0000-0000-000000000001',
      name: 'Voron',
      backend: PrinterBackend.Moonraker,
      motionType: MotionType.CoreXY,
      backendPort: 7125,
      inMaintenance: false,
      isEnabled: true,
      isOnline: false,
      obicoEnabled: false,
      hasCatalogUpdate: false,
      useModelDispatchDefaults: true,
    } satisfies Printer;
    const fastResponse: PrinterFast = response;

    expect(response.backend).toBe('Moonraker');
    expect(response.motionType).toBe('CoreXY');
    expect(fastResponse.isOnline).toBe(false);
    expect(response).not.toHaveProperty('backendUrl');
    expect(response).not.toHaveProperty('isReachable');
  });

  it('accepts a detail response without connection URLs or client-only reachability', () => {
    // PrinterDto: BackendUrl is JsonIgnore; IsReachable is not a DTO member.
    const response = {
      id: '00000000-0000-0000-0000-000000000002',
      name: 'Prusa MK4',
      backend: PrinterBackend.PrusaLink,
      isOnline: true,
      state: 'idle',
    } satisfies Printer;

    expect(response.backend).toBe('PrusaLink');
    expect(response.isOnline).toBe(true);
    expect(response).not.toHaveProperty('backendUrl');
    expect(response).not.toHaveProperty('isReachable');
  });
});
