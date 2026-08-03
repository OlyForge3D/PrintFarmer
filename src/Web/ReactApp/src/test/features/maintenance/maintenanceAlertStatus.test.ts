/**
 * Regression tests for the maintenance alert status wire contract.
 *
 * The API registers a bare `JsonStringEnumConverter` (ControllerStartup.cs),
 * so `MaintenanceAlertStatus` crosses the wire as its PascalCase member name
 * ("Active"), not as a number. `GET /api/maintenance/alerts` returns the raw
 * `MaintenanceAlert` entity, so this applies to every alert payload.
 *
 * When the TypeScript enum was declared numerically, every
 * `alert.status === MaintenanceAlertStatus.Active` comparison evaluated to
 * false, so the web UI reported zero alerts no matter what the API returned.
 */

import { describe, expect, it } from 'vitest';
import type { MaintenanceAlert } from '@/types/maintenance';
import { MaintenanceAlertStatus } from '@/types/maintenance';

describe('MaintenanceAlertStatus wire contract', () => {
  it('uses the PascalCase strings the API actually sends', () => {
    expect(MaintenanceAlertStatus.Active).toBe('Active');
    expect(MaintenanceAlertStatus.Acknowledged).toBe('Acknowledged');
    expect(MaintenanceAlertStatus.Resolved).toBe('Resolved');
    expect(MaintenanceAlertStatus.Dismissed).toBe('Dismissed');
  });

  it('is not declared numerically', () => {
    for (const value of Object.values(MaintenanceAlertStatus)) {
      expect(typeof value).toBe('string');
    }
  });
});

describe('active-alert filtering against a realistic API payload', () => {
  /** Mirrors the JSON shape returned by GET /api/maintenance/alerts. */
  const payload = [
    { id: 'a1', status: 'Active', severity: 4 },
    { id: 'a2', status: 'Acknowledged', severity: 2 },
    { id: 'a3', status: 'Resolved', severity: 1 },
    { id: 'a4', status: 'Dismissed', severity: 1 },
  ] as unknown as MaintenanceAlert[];

  // The predicate used by useMaintenanceAlerts / useMaintenanceStats /
  // PrinterMaintenancePage to decide what counts as "active".
  const isActive = (alert: MaintenanceAlert) =>
    alert.status === MaintenanceAlertStatus.Active ||
    alert.status === MaintenanceAlertStatus.Acknowledged;

  it('keeps Active and Acknowledged alerts', () => {
    expect(payload.filter(isActive).map((a) => a.id)).toEqual(['a1', 'a2']);
  });

  it('drops Resolved and Dismissed alerts', () => {
    const kept = payload.filter(isActive);
    expect(kept.some((a) => a.status === MaintenanceAlertStatus.Resolved)).toBe(false);
    expect(kept.some((a) => a.status === MaintenanceAlertStatus.Dismissed)).toBe(false);
  });

  it('does not silently discard every alert', () => {
    // The exact symptom of the numeric-enum bug: a non-empty API response
    // filtered down to nothing, surfacing as "0 alerts" in the dashboard.
    expect(payload.filter(isActive).length).toBeGreaterThan(0);
  });
});
