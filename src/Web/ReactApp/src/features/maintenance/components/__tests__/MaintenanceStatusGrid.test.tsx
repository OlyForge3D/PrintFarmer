import '@testing-library/jest-dom';
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { MaintenanceStatusGrid } from '../MaintenanceStatusGrid';
import type { PrinterMaintenanceStatus } from '../../hooks/useMaintenanceStats';

describe('MaintenanceStatusGrid', () => {
  it('labels each severity count for assistive technology', () => {
    const printer: PrinterMaintenanceStatus = {
      printerId: 'printer-1',
      printerName: 'Test printer',
      isOnline: true,
      inMaintenance: false,
      activeAlertCount: 10,
      criticalAlerts: 1,
      highAlerts: 2,
      mediumAlerts: 3,
      lowAlerts: 4,
    };

    render(<MaintenanceStatusGrid printers={[printer]} />);

    const printerCard = screen.getByRole('button', { name: /Test printer/ });
    expect(printerCard).toHaveAccessibleName(/Critical alerts: 1/);
    expect(printerCard).toHaveAccessibleName(/High alerts: 2/);
    expect(printerCard).toHaveAccessibleName(/Medium alerts: 3/);
    expect(printerCard).toHaveAccessibleName(/Low alerts: 4/);
  });
});
