import axios from "axios";

const API_BASE_URL =
  import.meta.env.VITE_API_BASE_URL || "http://localhost:5245";

/**
 * PrusaSlicer 2.9.x profiles service
 *
 * Stub service - to be implemented when PrusaSlicer support is needed
 */
export const prusaProfilesService = {
  async previewBundle(bundleJson: string) {
    console.warn("[prusaProfilesService] Preview not implemented");
    return { printers: [], materials: [], processes: [] };
  },

  async importBundle(request: { bundleJson: string }) {
    console.warn("[prusaProfilesService] Import not implemented");
    return { success: false, errors: ["Not yet implemented"] };
  },

  async exportBundle() {
    const timestamp = new Date().toISOString().split("T")[0];
    return JSON.stringify({ exported: timestamp }, null, 2);
  },
};
