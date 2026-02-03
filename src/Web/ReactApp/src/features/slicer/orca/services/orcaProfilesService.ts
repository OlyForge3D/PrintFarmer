import { apiClient } from "@/services/api";
import {
  OrcaBundlePreview,
  ImportOrcaBundleRequest,
  ImportOrcaBundleResult,
  OrcaBundleMappingResult,
  ExportOrcaBundleRequest,
} from "../types/orcaProfiles";

export const orcaProfilesService = {
  /**
   * Preview an OrcaSlicer bundle without importing.
   * Returns structured preview of all detected presets.
   */
  async previewBundle(bundleJson: string): Promise<OrcaBundlePreview> {
    return apiClient.post<OrcaBundlePreview>(
      `/api/slicer/profiles/import/orca/preview`,
      { bundleJson }
    );
  },

  /**
   * Import selected presets from an OrcaSlicer bundle.
   * Returns import result with counts and any errors/warnings.
   */
  async importBundle(
    request: ImportOrcaBundleRequest
  ): Promise<ImportOrcaBundleResult> {
    return apiClient.post<ImportOrcaBundleResult>(
      `/api/slicer/profiles/import/orca`,
      request
    );
  },

  /**
   * Export PrintFarmer profiles to OrcaSlicer config bundle format.
   * Returns a valid OrcaSlicer JSON bundle string.
   */
  async exportBundle(request?: ExportOrcaBundleRequest): Promise<string> {
    return apiClient.post<string>(
      `/api/slicer/profiles/export/orca`,
      request || {}
    );
  },

  /**
   * Get mapping results for bundle presets (matches to catalog entities).
   */
  async mapBundlePresets(
    preview: OrcaBundlePreview
  ): Promise<OrcaBundleMappingResult> {
    return apiClient.post<OrcaBundleMappingResult>(
      `/api/slicer/profiles/import/orca/map`,
      preview
    );
  },
};
