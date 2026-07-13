import { describe, it, expect, vi, beforeEach } from "vitest";

vi.mock("@/services/api", () => ({
  apiClient: {
    get: vi.fn(),
  },
}));

import { apiClient } from "@/services/api";
import { filamentCoverageService } from "../service";

const mockGet = apiClient.get as unknown as ReturnType<typeof vi.fn>;

describe("filamentCoverageService", () => {
  beforeEach(() => {
    mockGet.mockReset();
  });

  it("decodes the fleet payload", async () => {
    mockGet.mockResolvedValueOnce({
      data: {
        printers: [{ printerId: "p-1", status: "covers", toolheads: [] }],
        evaluatedAtUtc: "2025-01-01T00:00:00Z",
      },
    });
    const result = await filamentCoverageService.getFleetCoverage();
    expect(mockGet).toHaveBeenCalledWith(
      "/printers/filament-coverage",
      { signal: undefined },
    );
    expect(result?.printers).toHaveLength(1);
    expect(result?.printers[0].status).toBe("covers");
  });

  it("returns null when the feature is disabled (fleet 404)", async () => {
    mockGet.mockRejectedValueOnce({ response: { status: 404 } });
    const result = await filamentCoverageService.getFleetCoverage();
    expect(mockGet).toHaveBeenCalledWith("/printers/filament-coverage", {
      signal: undefined,
    });
    expect(result).toBeNull();
  });

  it("returns null when the feature is disabled with wrapper statusCode", async () => {
    mockGet.mockRejectedValueOnce({ statusCode: 404 });
    const result = await filamentCoverageService.getFleetCoverage();
    expect(mockGet).toHaveBeenCalledWith("/printers/filament-coverage", {
      signal: undefined,
    });
    expect(result).toBeNull();
  });

  it("propagates non-404 errors", async () => {
    const err = { response: { status: 500 }, message: "boom" };
    mockGet.mockRejectedValueOnce(err);
    await expect(filamentCoverageService.getFleetCoverage()).rejects.toBe(err);
    expect(mockGet).toHaveBeenCalledWith("/printers/filament-coverage", {
      signal: undefined,
    });
  });

  it("encodes the printer id in the URL", async () => {
    mockGet.mockResolvedValueOnce({
      data: { printerId: "p /1", printerName: "n", status: "covers", toolheads: [] },
    });
    await filamentCoverageService.getPrinterCoverage("p /1");
    expect(mockGet).toHaveBeenCalledWith(
      "/printers/p%20%2F1/filament-coverage",
      { signal: undefined },
    );
  });

  it("returns null when per-printer coverage 404s", async () => {
    mockGet.mockRejectedValueOnce({ response: { status: 404 } });
    const result = await filamentCoverageService.getPrinterCoverage("nope");
    expect(mockGet).toHaveBeenCalledWith(
      "/printers/nope/filament-coverage",
      { signal: undefined },
    );
    expect(result).toBeNull();
  });
});
