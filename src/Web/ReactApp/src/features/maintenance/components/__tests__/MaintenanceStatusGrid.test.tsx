import '@testing-library/jest-dom';
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { MaintenanceStatusGrid } from '../MaintenanceStatusGrid';
import type { PrinterMaintenanceStatus } from '../../hooks/useMaintenanceStats';

describe('MaintenanceStatusGrid', () => {
  it.each([
    {
      counts: [1, 2, 3, 4],
      total: 10,
      name: 'Test printer Online 10 alerts Critical alerts: 1; High alerts: 2; Medium alerts: 3; Low alerts: 4',
    },
    {
      counts: [0, 1, 0, 0],
      total: 1,
      name: 'Test printer Online 1 alert High alerts: 1',
    },
    {
      counts: [0, 0, 2, 3],
      total: 5,
      name: 'Test printer Online 5 alerts Medium alerts: 2; Low alerts: 3',
    },
    {
      counts: [0, 0, 0, 0],
      total: 0,
      name: 'Test printer Online Healthy',
    },
  ])('exposes the complete card name: $name', ({ counts, total, name }) => {
    const printer: PrinterMaintenanceStatus = {
      printerId: 'printer-1',
      printerName: 'Test printer',
      isOnline: true,
      inMaintenance: false,
      activeAlertCount: total,
      criticalAlerts: counts[0],
      highAlerts: counts[1],
      mediumAlerts: counts[2],
      lowAlerts: counts[3],
    };

    render(<MaintenanceStatusGrid printers={[printer]} />);

    expect(screen.getByRole('button')).toHaveAccessibleName(name);
  });
});
