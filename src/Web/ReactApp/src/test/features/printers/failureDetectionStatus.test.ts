import { describe, expect, it } from "vitest";
import {
  getFailureDetectionDisplayState,
  getFailureDetectionAttentionContent,
  getFailureDetectionStateLabel,
  getFailureDetectionStateVariant,
  getFailureDetectionSourceLabel,
  formatFailureDetectionTimestamp,
  getFailureDetectionDetail,
  getFailureDetectionIconColorClass,
} from "@/features/printers/utils/failureDetectionStatus";
import type { FailureDetectionPrinterStatusDto } from "@/types/api";

describe("failureDetectionStatus utilities", () => {
  it("preserves held reservations separately from unknown activity and monitoring", () => {
    const status: FailureDetectionPrinterStatusDto = {
      printerId: "printer-1",
      printerName: "Pool printer",
      state: "reservation-held",
      reason: "Printer activity is unknown or offline. Obico reservation retained.",
      isPrinting: false,
      detectionSource: "pooled",
      detectionTarget: "Pool A",
      lastOutcome: "healthy",
    };
    expect(getFailureDetectionDisplayState(status)).toBe("reservation-held");
    expect(getFailureDetectionStateLabel(status.state, true)).toBe("Reservation held");
    expect(getFailureDetectionStateVariant(status.state)).toBe("warning");
    expect(getFailureDetectionIconColorClass(status.state)).toBe("text-pf-warning");
    expect(getFailureDetectionDetail(status, true)).toBe(status.reason);
    expect(getFailureDetectionAttentionContent(status)?.action).toContain("physically verifying the printer is idle");
    const unknown = { ...status, state: "unknown", detectionSource: "none", detectionTarget: undefined };
    expect(getFailureDetectionDisplayState(unknown)).toBe("unknown");
    expect(getFailureDetectionStateLabel(unknown.state, true)).toBe("Activity unknown");
    expect(getFailureDetectionAttentionContent(unknown)).toBeNull();
  });

  describe("getFailureDetectionDisplayState", () => {
    it("returns undefined when status is missing", () => {
      expect(getFailureDetectionDisplayState(undefined)).toBeUndefined();
    });

    it("suppresses attention state before monitoring starts", () => {
      const status: FailureDetectionPrinterStatusDto = {
        printerId: "printer-1",
        printerName: "Voron 2.4",
        state: "error",
        reason: "Failed to contact Obico ML service.",
        isPrinting: false,
        detectionSource: "global",
        lastOutcome: "error",
        lastAnalyzedAt: "2026-01-15T10:30:00Z",
        lastConfidence: undefined,
      };

      expect(getFailureDetectionDisplayState(status)).toBeUndefined();
    });

    it("preserves error state while a print is actively being monitored", () => {
      const status: FailureDetectionPrinterStatusDto = {
        printerId: "printer-1",
        printerName: "Voron 2.4",
        state: "error",
        reason: "Failed to contact Obico ML service.",
        isPrinting: true,
        detectionSource: "global",
        lastOutcome: "error",
        lastAnalyzedAt: "2026-01-15T10:30:00Z",
        lastConfidence: undefined,
      };

      expect(getFailureDetectionDisplayState(status)).toBe("error");
    });
  });

  describe("getFailureDetectionStateLabel", () => {
    it('returns "Guarding" for monitoring state', () => {
      expect(getFailureDetectionStateLabel("monitoring")).toBe("Guarding");
    });

    it('returns "Ready" for idle state', () => {
      expect(getFailureDetectionStateLabel("idle")).toBe("Ready");
    });

    it('returns "Needs setup" for misconfigured state', () => {
      expect(getFailureDetectionStateLabel("misconfigured")).toBe(
        "Needs setup",
      );
    });

    it('returns "Monitor error" for error state', () => {
      expect(getFailureDetectionStateLabel("error")).toBe("Monitor error");
    });

    it('returns "Standby" for disabled state when enabled is true', () => {
      expect(getFailureDetectionStateLabel("disabled", true)).toBe("Standby");
    });

    it('returns "Off" for disabled state when enabled is false', () => {
      expect(getFailureDetectionStateLabel("disabled", false)).toBe("Off");
    });

    it('returns "Checking" for undefined state when enabled is true', () => {
      expect(getFailureDetectionStateLabel(undefined, true)).toBe("Checking");
    });

    it('returns "Off" for undefined state when enabled is false', () => {
      expect(getFailureDetectionStateLabel(undefined, false)).toBe("Off");
    });

    it('returns "Activity unknown" for unknown activity', () => {
      expect(getFailureDetectionStateLabel("unknown", true)).toBe("Activity unknown");
    });

    it('returns "Off" for unknown state when enabled is false', () => {
      expect(getFailureDetectionStateLabel("unrecognized", false)).toBe("Off");
    });
  });

  describe("getFailureDetectionStateVariant", () => {
    it('returns "success" for monitoring state', () => {
      expect(getFailureDetectionStateVariant("monitoring")).toBe("success");
    });

    it('returns "primary" for idle state', () => {
      expect(getFailureDetectionStateVariant("idle")).toBe("primary");
    });

    it('returns "warning" for misconfigured state', () => {
      expect(getFailureDetectionStateVariant("misconfigured")).toBe("warning");
    });

    it('returns "error" for error state', () => {
      expect(getFailureDetectionStateVariant("error")).toBe("error");
    });

    it('returns "default" for disabled state', () => {
      expect(getFailureDetectionStateVariant("disabled", true)).toBe("default");
      expect(getFailureDetectionStateVariant("disabled", false)).toBe(
        "default",
      );
    });

    it('returns "info" for undefined state when enabled is true', () => {
      expect(getFailureDetectionStateVariant(undefined, true)).toBe("info");
    });

    it('returns "default" for undefined state when enabled is false', () => {
      expect(getFailureDetectionStateVariant(undefined, false)).toBe("default");
    });
  });

  describe("getFailureDetectionSourceLabel", () => {
    it('returns "Pooled" for pooled source', () => {
      expect(getFailureDetectionSourceLabel("pooled")).toBe("Pooled");
    });

    it('returns "Global" for global source', () => {
      expect(getFailureDetectionSourceLabel("global")).toBe("Global");
    });

    it('returns "PrintGuard" for PrintGuard source', () => {
      expect(getFailureDetectionSourceLabel("printguard")).toBe("PrintGuard");
    });

    it('returns "Multiple" for multiple provider sources', () => {
      expect(getFailureDetectionSourceLabel("multiple")).toBe("Multiple");
    });

    it("returns null for none source", () => {
      expect(getFailureDetectionSourceLabel("none")).toBeNull();
    });

    it("returns null for undefined source", () => {
      expect(getFailureDetectionSourceLabel(undefined)).toBeNull();
    });

    it("returns null for unknown source", () => {
      expect(getFailureDetectionSourceLabel("unknown")).toBeNull();
    });
  });

  describe("formatFailureDetectionTimestamp", () => {
    it("returns null for undefined timestamp", () => {
      expect(formatFailureDetectionTimestamp(undefined)).toBeNull();
    });

    it("returns null for empty string timestamp", () => {
      expect(formatFailureDetectionTimestamp("")).toBeNull();
    });

    it("formats valid ISO timestamp as locale time string", () => {
      const result = formatFailureDetectionTimestamp("2026-01-15T10:30:00Z");
      expect(result).toBeTruthy();
      expect(result).toMatch(/\d{1,2}:\d{2}/); // Should contain time format
    });

    it("returns original string for invalid timestamp", () => {
      expect(formatFailureDetectionTimestamp("not-a-date")).toBe("not-a-date");
    });
  });

  describe("getFailureDetectionDetail", () => {
    describe("when status is undefined", () => {
      it("returns checking message when enabled is true", () => {
        const detail = getFailureDetectionDetail(undefined, true);
        expect(detail).toBe(
          "Checking whether the current print is actively being watched.",
        );
      });

      it("returns disabled message when enabled is false", () => {
        const detail = getFailureDetectionDetail(undefined, false);
        expect(detail).toBe("Failure detection is disabled for this printer.");
      });
    });

    describe("when state is not monitoring", () => {
      it("returns reason for idle state", () => {
        const status: FailureDetectionPrinterStatusDto = {
          printerId: "printer-1",
          printerName: "Voron 2.4",
          state: "idle",
          reason: "Printer is not printing.",
          isPrinting: false,
          detectionSource: "none",
          lastOutcome: "none",
          lastAnalyzedAt: undefined,
          lastConfidence: undefined,
        };

        expect(getFailureDetectionDetail(status, true)).toBe(
          "Printer is not printing.",
        );
      });

      it("returns reason for misconfigured state", () => {
        const status: FailureDetectionPrinterStatusDto = {
          printerId: "printer-1",
          printerName: "Voron 2.4",
          state: "misconfigured",
          reason: "No enabled camera snapshot URL is configured.",
          isPrinting: false,
          detectionSource: "none",
          lastOutcome: "none",
          lastAnalyzedAt: undefined,
          lastConfidence: undefined,
        };

        expect(getFailureDetectionDetail(status, true)).toBe(
          "No enabled camera snapshot URL is configured.",
        );
      });

      it("returns reason for error state", () => {
        const status: FailureDetectionPrinterStatusDto = {
          printerId: "printer-1",
          printerName: "Voron 2.4",
          state: "error",
          reason: "Failed to contact Obico ML service.",
          isPrinting: true,
          detectionSource: "global",
          lastOutcome: "error",
          lastAnalyzedAt: "2026-01-15T10:30:00Z",
          lastConfidence: undefined,
        };

        expect(getFailureDetectionDetail(status, true)).toBe(
          "Failed to contact Obico ML service.",
        );
      });

      it("returns checking detail for non-printing error state", () => {
        const status: FailureDetectionPrinterStatusDto = {
          printerId: "printer-1",
          printerName: "Voron 2.4",
          state: "error",
          reason: "Failed to contact Obico ML service.",
          isPrinting: false,
          detectionSource: "global",
          lastOutcome: "error",
          lastAnalyzedAt: "2026-01-15T10:30:00Z",
          lastConfidence: undefined,
        };

        expect(getFailureDetectionDetail(status, true)).toBe(
          "Checking whether the current print is actively being watched.",
        );
      });
    });

    describe("when state is monitoring", () => {
      it("returns reason when lastAnalyzedAt is null", () => {
        const status: FailureDetectionPrinterStatusDto = {
          printerId: "printer-1",
          printerName: "Voron 2.4",
          state: "monitoring",
          reason: "Monitoring via global Obico ML settings.",
          isPrinting: true,
          detectionSource: "global",
          lastOutcome: "none",
          lastAnalyzedAt: undefined,
          lastConfidence: undefined,
        };

        expect(getFailureDetectionDetail(status, true)).toBe(
          "Monitoring via global Obico ML settings.",
        );
      });

      it("includes scan time for healthy outcome", () => {
        const status: FailureDetectionPrinterStatusDto = {
          printerId: "printer-1",
          printerName: "Voron 2.4",
          state: "monitoring",
          reason: "Monitoring via global Obico ML settings.",
          isPrinting: true,
          detectionSource: "global",
          lastOutcome: "healthy",
          lastAnalyzedAt: "2026-01-15T10:30:00Z",
          lastConfidence: undefined,
        };

        const detail = getFailureDetectionDetail(status, true);
        expect(detail).toMatch(/Last scan .* • no failure detected/);
      });

      it("includes confidence percentage for failure outcome", () => {
        const status: FailureDetectionPrinterStatusDto = {
          printerId: "printer-1",
          printerName: "Voron 2.4",
          state: "monitoring",
          reason: "Monitoring via global Obico ML settings.",
          isPrinting: true,
          detectionSource: "global",
          lastOutcome: "failure",
          lastAnalyzedAt: "2026-01-15T10:30:00Z",
          lastConfidence: 0.87,
        };

        const detail = getFailureDetectionDetail(status, true);
        expect(detail).toMatch(
          /Last scan .* • failure detected • 87% confidence/,
        );
      });

      it("does not claim that failure detection paused the printer", () => {
        const status: FailureDetectionPrinterStatusDto = {
          printerId: "printer-1",
          printerName: "Voron 2.4",
          state: "monitoring",
          reason: "Monitoring via global Obico ML settings.",
          isPrinting: true,
          detectionSource: "global",
          lastOutcome: "failure",
          lastAnalyzedAt: "2026-01-15T10:30:00Z",
          lastConfidence: 0.92,
        };

        const detail = getFailureDetectionDetail(status, true);
        expect(detail).toMatch(/Last scan .* • failure detected • 92% confidence/);
        expect(detail).not.toContain("auto-paus");
      });

      it("omits confidence when null for failure outcome", () => {
        const status: FailureDetectionPrinterStatusDto = {
          printerId: "printer-1",
          printerName: "Voron 2.4",
          state: "monitoring",
          reason: "Monitoring via global Obico ML settings.",
          isPrinting: true,
          detectionSource: "global",
          lastOutcome: "failure",
          lastAnalyzedAt: "2026-01-15T10:30:00Z",
          lastConfidence: undefined,
        };

        const detail = getFailureDetectionDetail(status, true);
        expect(detail).toMatch(/Last scan .* • failure detected$/);
        expect(detail).not.toContain("confidence");
      });

      it("returns attention message for error outcome during monitoring", () => {
        const status: FailureDetectionPrinterStatusDto = {
          printerId: "printer-1",
          printerName: "Voron 2.4",
          state: "monitoring",
          reason: "Monitoring via global Obico ML settings.",
          isPrinting: true,
          detectionSource: "global",
          lastOutcome: "error",
          lastAnalyzedAt: "2026-01-15T10:30:00Z",
          lastConfidence: undefined,
        };

        const detail = getFailureDetectionDetail(status, true);
        expect(detail).toMatch(
          /Last scan .* • scan failed — check the camera feed or detection provider/,
        );
      });

      it("returns actively watching message for unknown outcome", () => {
        const status: FailureDetectionPrinterStatusDto = {
          printerId: "printer-1",
          printerName: "Voron 2.4",
          state: "monitoring",
          reason: "Monitoring via global Obico ML settings.",
          isPrinting: true,
          detectionSource: "global",
          lastOutcome: "unknown",
          lastAnalyzedAt: "2026-01-15T10:30:00Z",
          lastConfidence: undefined,
        };

        const detail = getFailureDetectionDetail(status, true);
        expect(detail).toMatch(/Last scan .* • actively watching this print/);
      });

      it("returns actively watching message for none outcome", () => {
        const status: FailureDetectionPrinterStatusDto = {
          printerId: "printer-1",
          printerName: "Voron 2.4",
          state: "monitoring",
          reason: "Monitoring via global Obico ML settings.",
          isPrinting: true,
          detectionSource: "global",
          lastOutcome: "none",
          lastAnalyzedAt: "2026-01-15T10:30:00Z",
          lastConfidence: undefined,
        };

        const detail = getFailureDetectionDetail(status, true);
        expect(detail).toMatch(/Last scan .* • actively watching this print/);
      });
    });
  });

  describe("getFailureDetectionAttentionContent", () => {
    it("returns explicit issue and action for misconfigured printers", () => {
      const status: FailureDetectionPrinterStatusDto = {
        printerId: "printer-1",
        printerName: "Voron 2.4",
        state: "misconfigured",
        reason: "No enabled camera snapshot URL is configured.",
        isPrinting: false,
        detectionSource: "none",
        lastOutcome: "none",
        lastAnalyzedAt: undefined,
        lastConfidence: undefined,
      };

      expect(getFailureDetectionAttentionContent(status)).toEqual({
        issue: "No enabled camera snapshot URL is configured.",
        action: "Add or enable a snapshot camera for this printer.",
      });
    });

    it("returns explicit issue and action for service errors", () => {
      const status: FailureDetectionPrinterStatusDto = {
        printerId: "printer-1",
        printerName: "Voron 2.4",
        state: "error",
        reason: "Failed to contact Obico ML service.",
        isPrinting: true,
        detectionSource: "global",
        lastOutcome: "error",
        lastAnalyzedAt: "2026-01-15T10:30:00Z",
        lastConfidence: undefined,
      };

      expect(getFailureDetectionAttentionContent(status)).toEqual({
        issue: "Failed to contact Obico ML service.",
        action:
          "Check the failure-detection service connection, then try again.",
      });
    });

    it("returns snapshot-network guidance for Obico reachability errors", () => {
      const status: FailureDetectionPrinterStatusDto = {
        printerId: "printer-1",
        printerName: "Voron 2.4",
        state: "error",
        reason:
          "Obico rejected the snapshot URL request (HTTP 400). Check whether the saved snapshot URL is reachable from the Obico server network and still returns an image.",
        isPrinting: true,
        detectionSource: "global",
        lastOutcome: "error",
        lastAnalyzedAt: "2026-01-15T10:30:00Z",
        lastConfidence: undefined,
      };

      expect(getFailureDetectionAttentionContent(status)).toEqual({
        issue:
          "Obico rejected the snapshot URL request (HTTP 400). Check whether the saved snapshot URL is reachable from the Obico server network and still returns an image.",
        action:
          "Make the saved snapshot URL reachable from the configured detection provider, or switch to a camera feed that PrintFarmer can fetch locally.",
      });
    });

    it("returns timeout guidance for snapshot fetch failures", () => {
      const status: FailureDetectionPrinterStatusDto = {
        printerId: "printer-1",
        printerName: "Voron 2.4",
        state: "error",
        reason:
          "Snapshot fetch timeout. PrintFarmer could not download the camera snapshot in time.",
        isPrinting: true,
        detectionSource: "global",
        lastOutcome: "error",
        lastAnalyzedAt: "2026-01-15T10:30:00Z",
        lastConfidence: undefined,
      };

      expect(getFailureDetectionAttentionContent(status)).toEqual({
        issue:
          "Snapshot fetch timeout. PrintFarmer could not download the camera snapshot in time.",
        action:
          "Open the latest snapshot and verify PrintFarmer can reach the camera feed quickly enough.",
      });
    });

    it("returns failed-scan guidance when monitoring stays active", () => {
      const status: FailureDetectionPrinterStatusDto = {
        printerId: "printer-1",
        printerName: "Voron 2.4",
        state: "monitoring",
        reason: "Monitoring via global Obico ML settings.",
        isPrinting: true,
        detectionSource: "global",
        lastOutcome: "error",
        lastAnalyzedAt: "2026-01-15T10:30:00Z",
        lastConfidence: undefined,
      };

      const attention = getFailureDetectionAttentionContent(status);

      expect(attention?.issue).toMatch(
        /The last failure-detection scan failed at/,
      );
      expect(attention?.action).toBe(
        "Check the camera feed or monitoring service before relying on failure alerts.",
      );
    });

    it("returns null when no action is needed", () => {
      const status: FailureDetectionPrinterStatusDto = {
        printerId: "printer-1",
        printerName: "Voron 2.4",
        state: "monitoring",
        reason: "Monitoring via global Obico ML settings.",
        isPrinting: true,
        detectionSource: "global",
        lastOutcome: "healthy",
        lastAnalyzedAt: "2026-01-15T10:30:00Z",
        lastConfidence: undefined,
      };

      expect(getFailureDetectionAttentionContent(status)).toBeNull();
    });
  });

  describe("getFailureDetectionIconColorClass", () => {
    it.each([
      ["monitoring", "text-pf-success"],
      ["idle", "text-pf-status-idle-text"],
      ["misconfigured", "text-pf-warning"],
      ["error", "text-pf-error"],
      ["disabled", "text-pf-control-disabled-text"],
    ] as const)("maps %s to %s", (state, expected) => {
      expect(getFailureDetectionIconColorClass(state)).toBe(expected);
      expect(getFailureDetectionIconColorClass(state, true)).toBe(expected);
    });

    it.each([
      [true, "text-pf-text-secondary"],
      [false, "text-pf-control-disabled-text"],
    ] as const)("maps an unknown state with enabled=%s to %s", (enabled, expected) => {
      expect(getFailureDetectionIconColorClass(undefined, enabled)).toBe(expected);
    });

    it("never uses the brand accent, which collides with status hues in some themes", () => {
      for (const state of ["monitoring", "idle", "misconfigured", "error", "disabled", undefined]) {
        for (const enabled of [true, false]) {
          expect(getFailureDetectionIconColorClass(state, enabled)).not.toMatch(/accent/);
        }
      }
    });

    it("reserves error red for the error state only", () => {
      for (const state of ["monitoring", "idle", "misconfigured", "disabled", "checking", undefined]) {
        expect(getFailureDetectionIconColorClass(state)).not.toBe("text-pf-error");
        expect(getFailureDetectionIconColorClass(state, true)).not.toBe("text-pf-error");
      }
    });
  });
});
