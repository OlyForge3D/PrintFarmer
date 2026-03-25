import { describe, expect, it } from 'vitest';
import {
  getFailureDetectionDisplayState,
  getFailureDetectionAttentionContent,
  getFailureDetectionStateLabel,
  getFailureDetectionStateVariant,
  getFailureDetectionSourceLabel,
  formatFailureDetectionTimestamp,
  getFailureDetectionDetail,
} from '@/features/printers/utils/failureDetectionStatus';
import type { FailureDetectionPrinterStatusDto } from '@/types/api';

describe('failureDetectionStatus utilities', () => {
  describe('getFailureDetectionDisplayState', () => {
    it('returns undefined when status is missing', () => {
      expect(getFailureDetectionDisplayState(undefined)).toBeUndefined();
    });

    it('suppresses attention state before monitoring starts', () => {
      const status: FailureDetectionPrinterStatusDto = {
        printerId: 'printer-1',
        printerName: 'Voron 2.4',
        state: 'error',
        reason: 'Failed to contact Obico ML service.',
        isPrinting: false,
        detectionSource: 'global',
        lastOutcome: 'error',
        lastAnalyzedAt: '2026-01-15T10:30:00Z',
        lastConfidence: null,
        lastAutoPaused: false,
      };

      expect(getFailureDetectionDisplayState(status)).toBeUndefined();
    });

    it('preserves error state while a print is actively being monitored', () => {
      const status: FailureDetectionPrinterStatusDto = {
        printerId: 'printer-1',
        printerName: 'Voron 2.4',
        state: 'error',
        reason: 'Failed to contact Obico ML service.',
        isPrinting: true,
        detectionSource: 'global',
        lastOutcome: 'error',
        lastAnalyzedAt: '2026-01-15T10:30:00Z',
        lastConfidence: null,
        lastAutoPaused: false,
      };

      expect(getFailureDetectionDisplayState(status)).toBe('error');
    });
  });

  describe('getFailureDetectionStateLabel', () => {
    it('returns "Guarding" for monitoring state', () => {
      expect(getFailureDetectionStateLabel('monitoring')).toBe('Guarding');
    });

    it('returns "Ready" for idle state', () => {
      expect(getFailureDetectionStateLabel('idle')).toBe('Ready');
    });

    it('returns "Needs setup" for misconfigured state', () => {
      expect(getFailureDetectionStateLabel('misconfigured')).toBe('Needs setup');
    });

    it('returns "Monitor error" for error state', () => {
      expect(getFailureDetectionStateLabel('error')).toBe('Monitor error');
    });

    it('returns "Standby" for disabled state when enabled is true', () => {
      expect(getFailureDetectionStateLabel('disabled', true)).toBe('Standby');
    });

    it('returns "Off" for disabled state when enabled is false', () => {
      expect(getFailureDetectionStateLabel('disabled', false)).toBe('Off');
    });

    it('returns "Checking" for undefined state when enabled is true', () => {
      expect(getFailureDetectionStateLabel(undefined, true)).toBe('Checking');
    });

    it('returns "Off" for undefined state when enabled is false', () => {
      expect(getFailureDetectionStateLabel(undefined, false)).toBe('Off');
    });

    it('returns "Checking" for unknown state when enabled is true', () => {
      expect(getFailureDetectionStateLabel('unknown', true)).toBe('Checking');
    });

    it('returns "Off" for unknown state when enabled is false', () => {
      expect(getFailureDetectionStateLabel('unknown', false)).toBe('Off');
    });
  });

  describe('getFailureDetectionStateVariant', () => {
    it('returns "success" for monitoring state', () => {
      expect(getFailureDetectionStateVariant('monitoring')).toBe('success');
    });

    it('returns "primary" for idle state', () => {
      expect(getFailureDetectionStateVariant('idle')).toBe('primary');
    });

    it('returns "warning" for misconfigured state', () => {
      expect(getFailureDetectionStateVariant('misconfigured')).toBe('warning');
    });

    it('returns "error" for error state', () => {
      expect(getFailureDetectionStateVariant('error')).toBe('error');
    });

    it('returns "default" for disabled state', () => {
      expect(getFailureDetectionStateVariant('disabled', true)).toBe('default');
      expect(getFailureDetectionStateVariant('disabled', false)).toBe('default');
    });

    it('returns "info" for undefined state when enabled is true', () => {
      expect(getFailureDetectionStateVariant(undefined, true)).toBe('info');
    });

    it('returns "default" for undefined state when enabled is false', () => {
      expect(getFailureDetectionStateVariant(undefined, false)).toBe('default');
    });
  });

  describe('getFailureDetectionSourceLabel', () => {
    it('returns "Pooled" for pooled source', () => {
      expect(getFailureDetectionSourceLabel('pooled')).toBe('Pooled');
    });

    it('returns "Global" for global source', () => {
      expect(getFailureDetectionSourceLabel('global')).toBe('Global');
    });

    it('returns null for none source', () => {
      expect(getFailureDetectionSourceLabel('none')).toBeNull();
    });

    it('returns null for undefined source', () => {
      expect(getFailureDetectionSourceLabel(undefined)).toBeNull();
    });

    it('returns null for unknown source', () => {
      expect(getFailureDetectionSourceLabel('unknown')).toBeNull();
    });
  });

  describe('formatFailureDetectionTimestamp', () => {
    it('returns null for undefined timestamp', () => {
      expect(formatFailureDetectionTimestamp(undefined)).toBeNull();
    });

    it('returns null for empty string timestamp', () => {
      expect(formatFailureDetectionTimestamp('')).toBeNull();
    });

    it('formats valid ISO timestamp as locale time string', () => {
      const result = formatFailureDetectionTimestamp('2026-01-15T10:30:00Z');
      expect(result).toBeTruthy();
      expect(result).toMatch(/\d{1,2}:\d{2}/); // Should contain time format
    });

    it('returns original string for invalid timestamp', () => {
      expect(formatFailureDetectionTimestamp('not-a-date')).toBe('not-a-date');
    });
  });

  describe('getFailureDetectionDetail', () => {
    describe('when status is undefined', () => {
      it('returns checking message when enabled is true', () => {
        const detail = getFailureDetectionDetail(undefined, true);
        expect(detail).toBe(
          'Checking whether the current print is actively being watched.'
        );
      });

      it('returns disabled message when enabled is false', () => {
        const detail = getFailureDetectionDetail(undefined, false);
        expect(detail).toBe('Failure detection is disabled for this printer.');
      });
    });

    describe('when state is not monitoring', () => {
      it('returns reason for idle state', () => {
        const status: FailureDetectionPrinterStatusDto = {
          state: 'idle',
          reason: 'Printer is not printing.',
          isPrinting: false,
          detectionSource: 'none',
          lastOutcome: 'none',
          lastAnalyzedAt: null,
          lastConfidence: null,
          lastAutoPaused: false,
        };

        expect(getFailureDetectionDetail(status, true)).toBe('Printer is not printing.');
      });

      it('returns reason for misconfigured state', () => {
        const status: FailureDetectionPrinterStatusDto = {
          state: 'misconfigured',
          reason: 'No enabled camera snapshot URL is configured.',
          isPrinting: false,
          detectionSource: 'none',
          lastOutcome: 'none',
          lastAnalyzedAt: null,
          lastConfidence: null,
          lastAutoPaused: false,
        };

        expect(getFailureDetectionDetail(status, true)).toBe(
          'No enabled camera snapshot URL is configured.'
        );
      });

      it('returns reason for error state', () => {
        const status: FailureDetectionPrinterStatusDto = {
          printerId: 'printer-1',
          printerName: 'Voron 2.4',
          state: 'error',
          reason: 'Failed to contact Obico ML service.',
          isPrinting: true,
          detectionSource: 'global',
          lastOutcome: 'error',
          lastAnalyzedAt: '2026-01-15T10:30:00Z',
          lastConfidence: null,
          lastAutoPaused: false,
        };

        expect(getFailureDetectionDetail(status, true)).toBe(
          'Failed to contact Obico ML service.'
        );
      });

      it('returns checking detail for non-printing error state', () => {
        const status: FailureDetectionPrinterStatusDto = {
          printerId: 'printer-1',
          printerName: 'Voron 2.4',
          state: 'error',
          reason: 'Failed to contact Obico ML service.',
          isPrinting: false,
          detectionSource: 'global',
          lastOutcome: 'error',
          lastAnalyzedAt: '2026-01-15T10:30:00Z',
          lastConfidence: null,
          lastAutoPaused: false,
        };

        expect(getFailureDetectionDetail(status, true)).toBe(
          'Checking whether the current print is actively being watched.'
        );
      });
    });

    describe('when state is monitoring', () => {
      it('returns reason when lastAnalyzedAt is null', () => {
        const status: FailureDetectionPrinterStatusDto = {
          state: 'monitoring',
          reason: 'Monitoring via global Obico ML settings.',
          isPrinting: true,
          detectionSource: 'global',
          lastOutcome: 'none',
          lastAnalyzedAt: null,
          lastConfidence: null,
          lastAutoPaused: false,
        };

        expect(getFailureDetectionDetail(status, true)).toBe(
          'Monitoring via global Obico ML settings.'
        );
      });

      it('includes scan time for healthy outcome', () => {
        const status: FailureDetectionPrinterStatusDto = {
          state: 'monitoring',
          reason: 'Monitoring via global Obico ML settings.',
          isPrinting: true,
          detectionSource: 'global',
          lastOutcome: 'healthy',
          lastAnalyzedAt: '2026-01-15T10:30:00Z',
          lastConfidence: null,
          lastAutoPaused: false,
        };

        const detail = getFailureDetectionDetail(status, true);
        expect(detail).toMatch(/Last scan .* • no failure detected/);
      });

      it('includes confidence percentage for failure outcome', () => {
        const status: FailureDetectionPrinterStatusDto = {
          state: 'monitoring',
          reason: 'Monitoring via global Obico ML settings.',
          isPrinting: true,
          detectionSource: 'global',
          lastOutcome: 'failure',
          lastAnalyzedAt: '2026-01-15T10:30:00Z',
          lastConfidence: 0.87,
          lastAutoPaused: false,
        };

        const detail = getFailureDetectionDetail(status, true);
        expect(detail).toMatch(/Last scan .* • failure detected • 87% confidence/);
      });

      it('includes auto-pause indicator when printer was paused', () => {
        const status: FailureDetectionPrinterStatusDto = {
          state: 'monitoring',
          reason: 'Monitoring via global Obico ML settings.',
          isPrinting: true,
          detectionSource: 'global',
          lastOutcome: 'failure',
          lastAnalyzedAt: '2026-01-15T10:30:00Z',
          lastConfidence: 0.92,
          lastAutoPaused: true,
        };

        const detail = getFailureDetectionDetail(status, true);
        expect(detail).toMatch(/Last scan .* • failure detected • 92% confidence • auto-paused/);
      });

      it('omits confidence when null for failure outcome', () => {
        const status: FailureDetectionPrinterStatusDto = {
          state: 'monitoring',
          reason: 'Monitoring via global Obico ML settings.',
          isPrinting: true,
          detectionSource: 'global',
          lastOutcome: 'failure',
          lastAnalyzedAt: '2026-01-15T10:30:00Z',
          lastConfidence: null,
          lastAutoPaused: false,
        };

        const detail = getFailureDetectionDetail(status, true);
        expect(detail).toMatch(/Last scan .* • failure detected$/);
        expect(detail).not.toContain('confidence');
      });

      it('returns attention message for error outcome during monitoring', () => {
        const status: FailureDetectionPrinterStatusDto = {
          state: 'monitoring',
          reason: 'Monitoring via global Obico ML settings.',
          isPrinting: true,
          detectionSource: 'global',
          lastOutcome: 'error',
          lastAnalyzedAt: '2026-01-15T10:30:00Z',
          lastConfidence: null,
          lastAutoPaused: false,
        };

        const detail = getFailureDetectionDetail(status, true);
        expect(detail).toMatch(/Last scan .* • scan failed — check the camera feed or ML service/);
      });

      it('returns actively watching message for unknown outcome', () => {
        const status: FailureDetectionPrinterStatusDto = {
          state: 'monitoring',
          reason: 'Monitoring via global Obico ML settings.',
          isPrinting: true,
          detectionSource: 'global',
          lastOutcome: 'unknown',
          lastAnalyzedAt: '2026-01-15T10:30:00Z',
          lastConfidence: null,
          lastAutoPaused: false,
        };

        const detail = getFailureDetectionDetail(status, true);
        expect(detail).toMatch(/Last scan .* • actively watching this print/);
      });

      it('returns actively watching message for none outcome', () => {
        const status: FailureDetectionPrinterStatusDto = {
          state: 'monitoring',
          reason: 'Monitoring via global Obico ML settings.',
          isPrinting: true,
          detectionSource: 'global',
          lastOutcome: 'none',
          lastAnalyzedAt: '2026-01-15T10:30:00Z',
          lastConfidence: null,
          lastAutoPaused: false,
        };

        const detail = getFailureDetectionDetail(status, true);
        expect(detail).toMatch(/Last scan .* • actively watching this print/);
      });
    });
  });

  describe('getFailureDetectionAttentionContent', () => {
    it('returns explicit issue and action for misconfigured printers', () => {
      const status: FailureDetectionPrinterStatusDto = {
        printerId: 'printer-1',
        printerName: 'Voron 2.4',
        state: 'misconfigured',
        reason: 'No enabled camera snapshot URL is configured.',
        isPrinting: false,
        detectionSource: 'none',
        lastOutcome: 'none',
        lastAnalyzedAt: null,
        lastConfidence: null,
        lastAutoPaused: false,
      };

      expect(getFailureDetectionAttentionContent(status)).toEqual({
        issue: 'No enabled camera snapshot URL is configured.',
        action: 'Add or enable a snapshot camera for this printer.',
      });
    });

    it('returns explicit issue and action for service errors', () => {
      const status: FailureDetectionPrinterStatusDto = {
        printerId: 'printer-1',
        printerName: 'Voron 2.4',
        state: 'error',
        reason: 'Failed to contact Obico ML service.',
        isPrinting: true,
        detectionSource: 'global',
        lastOutcome: 'error',
        lastAnalyzedAt: '2026-01-15T10:30:00Z',
        lastConfidence: null,
        lastAutoPaused: false,
      };

      expect(getFailureDetectionAttentionContent(status)).toEqual({
        issue: 'Failed to contact Obico ML service.',
        action: 'Check the failure-detection service connection, then try again.',
      });
    });

    it('returns snapshot-network guidance for Obico reachability errors', () => {
      const status: FailureDetectionPrinterStatusDto = {
        printerId: 'printer-1',
        printerName: 'Voron 2.4',
        state: 'error',
        reason: 'Obico rejected the snapshot URL request (HTTP 400). Check whether the saved snapshot URL is reachable from the Obico server network and still returns an image.',
        isPrinting: true,
        detectionSource: 'global',
        lastOutcome: 'error',
        lastAnalyzedAt: '2026-01-15T10:30:00Z',
        lastConfidence: null,
        lastAutoPaused: false,
      };

      expect(getFailureDetectionAttentionContent(status)).toEqual({
        issue: 'Obico rejected the snapshot URL request (HTTP 400). Check whether the saved snapshot URL is reachable from the Obico server network and still returns an image.',
        action: 'Make the saved snapshot URL reachable from the Obico server network, or switch to a camera feed that PrintFarmer can fetch locally.',
      });
    });

    it('returns timeout guidance for snapshot fetch failures', () => {
      const status: FailureDetectionPrinterStatusDto = {
        printerId: 'printer-1',
        printerName: 'Voron 2.4',
        state: 'error',
        reason: 'Snapshot fetch timeout. PrintFarmer could not download the camera snapshot in time.',
        isPrinting: true,
        detectionSource: 'global',
        lastOutcome: 'error',
        lastAnalyzedAt: '2026-01-15T10:30:00Z',
        lastConfidence: null,
        lastAutoPaused: false,
      };

      expect(getFailureDetectionAttentionContent(status)).toEqual({
        issue: 'Snapshot fetch timeout. PrintFarmer could not download the camera snapshot in time.',
        action: 'Open the latest snapshot and verify PrintFarmer can reach the camera feed quickly enough.',
      });
    });

    it('returns failed-scan guidance when monitoring stays active', () => {
      const status: FailureDetectionPrinterStatusDto = {
        printerId: 'printer-1',
        printerName: 'Voron 2.4',
        state: 'monitoring',
        reason: 'Monitoring via global Obico ML settings.',
        isPrinting: true,
        detectionSource: 'global',
        lastOutcome: 'error',
        lastAnalyzedAt: '2026-01-15T10:30:00Z',
        lastConfidence: null,
        lastAutoPaused: false,
      };

      const attention = getFailureDetectionAttentionContent(status);

      expect(attention?.issue).toMatch(/The last failure-detection scan failed at/);
      expect(attention?.action).toBe(
        'Check the camera feed or monitoring service before relying on automatic pause.'
      );
    });

    it('returns null when no action is needed', () => {
      const status: FailureDetectionPrinterStatusDto = {
        printerId: 'printer-1',
        printerName: 'Voron 2.4',
        state: 'monitoring',
        reason: 'Monitoring via global Obico ML settings.',
        isPrinting: true,
        detectionSource: 'global',
        lastOutcome: 'healthy',
        lastAnalyzedAt: '2026-01-15T10:30:00Z',
        lastConfidence: null,
        lastAutoPaused: false,
      };

      expect(getFailureDetectionAttentionContent(status)).toBeNull();
    });
  });
});
