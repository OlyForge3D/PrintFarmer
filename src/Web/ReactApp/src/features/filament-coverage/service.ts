/**
 * Filament coverage API service (issue #717).
 * Wraps `apiClient` and decodes payloads into canonical domain types.
 * When the coverage feature is disabled the API returns 404 — callers
 * receive `null` and must fall back to whatever spool data they already
 * have. All other errors bubble as thrown promises.
 */
import { apiClient } from "@/services/api";
import {
  decodeFleetFilamentCoverage,
  decodePrinterFilamentCoverage,
  type FleetFilamentCoverage,
  type PrinterFilamentCoverage,
} from "./types";

const PRINTER_COVERAGE_URL = (printerId: string) =>
  `/printers/${encodeURIComponent(printerId)}/filament-coverage`;
const FLEET_COVERAGE_URL = "/printers/filament-coverage";

interface AxiosLikeError {
  response?: { status?: number };
}

function is404(error: unknown): boolean {
  const err = error as AxiosLikeError | undefined;
  const status = err?.response?.status;
  if (status === 404) return true;
  const nested = (error as { statusCode?: number } | undefined)?.statusCode;
  return nested === 404;
}

export const filamentCoverageService = {
  /**
   * Fetch fleet coverage. Returns `null` when the feature is disabled (404).
   */
  async getFleetCoverage(signal?: AbortSignal): Promise<FleetFilamentCoverage | null> {
    try {
      const response = await apiClient.get<unknown>(FLEET_COVERAGE_URL, { signal });
      return decodeFleetFilamentCoverage(response.data);
    } catch (error) {
      if (is404(error)) return null;
      throw error;
    }
  },

  /**
   * Fetch coverage for a single printer. Returns `null` when the feature
   * is disabled or the printer is not found (both surface as 404).
   */
  async getPrinterCoverage(
    printerId: string,
    signal?: AbortSignal,
  ): Promise<PrinterFilamentCoverage | null> {
    try {
      const response = await apiClient.get<unknown>(PRINTER_COVERAGE_URL(printerId), { signal });
      return decodePrinterFilamentCoverage(response.data);
    } catch (error) {
      if (is404(error)) return null;
      throw error;
    }
  },
};
