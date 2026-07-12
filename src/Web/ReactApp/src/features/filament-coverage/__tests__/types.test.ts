import { describe, it, expect } from "vitest";
import {
  decodeFilamentCoverageChangedEvent,
  decodeFilamentCoverageStatus,
  decodeFleetFilamentCoverage,
  decodePrinterFilamentCoverage,
  decodeToolheadCoverage,
} from "../types";

describe("decodeFilamentCoverageStatus", () => {
  it("accepts canonical lowercase tokens", () => {
    expect(decodeFilamentCoverageStatus("covers")).toBe("covers");
    expect(decodeFilamentCoverageStatus("runout")).toBe("runout");
    expect(decodeFilamentCoverageStatus("unknown")).toBe("unknown");
  });

  it("tolerates legacy PascalCase variants at the boundary", () => {
    expect(decodeFilamentCoverageStatus("Covers")).toBe("covers");
    expect(decodeFilamentCoverageStatus("Runout")).toBe("runout");
    expect(decodeFilamentCoverageStatus("Unknown")).toBe("unknown");
  });

  it("maps legacy 'Insufficient' to canonical 'runout'", () => {
    expect(decodeFilamentCoverageStatus("Insufficient")).toBe("runout");
    expect(decodeFilamentCoverageStatus("insufficient")).toBe("runout");
  });

  it("returns 'unknown' for anything else (never invents runout)", () => {
    expect(decodeFilamentCoverageStatus("")).toBe("unknown");
    expect(decodeFilamentCoverageStatus(undefined)).toBe("unknown");
    expect(decodeFilamentCoverageStatus(null)).toBe("unknown");
    expect(decodeFilamentCoverageStatus("something-else")).toBe("unknown");
    // must not accidentally interpret truthiness as a positive status
    expect(decodeFilamentCoverageStatus("COVERS ")).toBe("unknown");
  });
});

describe("decodeToolheadCoverage", () => {
  it("maps camelCase fields and preserves nulls", () => {
    const raw = {
      toolheadIndex: 1,
      toolheadName: "Extruder 2",
      spoolId: 42,
      material: "PETG",
      filamentColor: "#00ff00",
      remainingGrams: 350,
      currentJobRequiredGrams: 800,
      currentJobRemainingGrams: 400,
      queuedRequiredGrams: null,
      totalDemandGrams: null,
      status: "runout",
      statusReason: "insufficient-remaining",
      predictedRunoutAt: "2025-01-02T03:04:05Z",
      predictedRunoutLayer: 123,
    };
    const decoded = decodeToolheadCoverage(raw);
    expect(decoded).toMatchObject({
      toolheadIndex: 1,
      toolheadName: "Extruder 2",
      spoolId: 42,
      status: "runout",
      statusReason: "insufficient-remaining",
      predictedRunoutLayer: 123,
    });
    expect(decoded.queuedRequiredGrams).toBeNull();
    expect(decoded.totalDemandGrams).toBeNull();
  });

  it("defaults missing fields sensibly", () => {
    const decoded = decodeToolheadCoverage({});
    expect(decoded.toolheadIndex).toBe(0);
    expect(decoded.status).toBe("unknown");
    expect(decoded.spoolId).toBeNull();
    expect(decoded.predictedRunoutAt).toBeNull();
  });
});

describe("decodePrinterFilamentCoverage", () => {
  it("decodes the printer envelope including toolheads", () => {
    const raw = {
      printerId: "p-1",
      printerName: "Alpha",
      status: "Runout",
      toolheads: [
        { toolheadIndex: 0, status: "covers" },
        { toolheadIndex: 1, status: "Runout", predictedRunoutAt: "2025-01-01T00:00:00Z" },
      ],
      activeJobId: "j-1",
      activeJobName: "cube.gcode",
      activeJobProgress: 42,
      earliestPredictedRunoutAt: "2025-01-01T00:00:00Z",
      assignedQueuedJobCount: 3,
      evaluatedAtUtc: "2025-01-01T00:00:00Z",
    };
    const decoded = decodePrinterFilamentCoverage(raw);
    expect(decoded.printerId).toBe("p-1");
    expect(decoded.status).toBe("runout");
    expect(decoded.toolheads).toHaveLength(2);
    expect(decoded.toolheads[1].status).toBe("runout");
    expect(decoded.toolheads[1].predictedRunoutAt).toBe("2025-01-01T00:00:00Z");
    expect(decoded.assignedQueuedJobCount).toBe(3);
  });
});

describe("decodeFleetFilamentCoverage", () => {
  it("decodes an empty fleet gracefully", () => {
    const decoded = decodeFleetFilamentCoverage({ printers: [], evaluatedAtUtc: "2025-01-01T00:00:00Z" });
    expect(decoded.printers).toEqual([]);
  });

  it("decodes non-array printers as empty", () => {
    const decoded = decodeFleetFilamentCoverage({ printers: null });
    expect(decoded.printers).toEqual([]);
  });
});

describe("decodeFilamentCoverageChangedEvent", () => {
  it("passes canonical reasons through unchanged", () => {
    const evt = decodeFilamentCoverageChangedEvent({
      printerId: "p-1",
      reason: "jobProgress",
      occurredAt: "2025-01-01T00:00:00Z",
    });
    expect(evt).toEqual({
      printerId: "p-1",
      reason: "jobProgress",
      occurredAt: "2025-01-01T00:00:00Z",
    });
  });

  it("treats missing printerId as fleet-wide (null)", () => {
    const evt = decodeFilamentCoverageChangedEvent({ reason: "thresholdChanged" });
    expect(evt.printerId).toBeNull();
    expect(evt.reason).toBe("thresholdChanged");
    expect(typeof evt.occurredAt).toBe("string");
  });
});
