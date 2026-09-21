import { client } from "@/services/api/httpClient";
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
    const response = await client.post<OrcaBundlePreview>(
      `/slicer/profiles/import/orca/preview`,
      { bundleJson }
    );
    return response.data;
  },

  /**
   * Import selected presets from an OrcaSlicer bundle.
   * Returns import result with counts and any errors/warnings.
   */
  async importBundle(
    request: ImportOrcaBundleRequest
  ): Promise<ImportOrcaBundleResult> {
    const response = await client.post<ImportOrcaBundleResult>(
      `/slicer/profiles/import/orca`,
      request
    );
    return response.data;
  },

  /**
   * Export PrintFarmer profiles to OrcaSlicer config bundle format.
   * Returns a valid OrcaSlicer JSON bundle string.
   */
  async exportBundle(request?: ExportOrcaBundleRequest): Promise<string> {
    const response = await client.post<string>(
      `/slicer/profiles/export/orca`,
      request || {}
    );
    return response.data;
  },

  /**
   * Get mapping results for bundle presets (matches to catalog entities).
   */
  async mapBundlePresets(
    preview: OrcaBundlePreview
  ): Promise<OrcaBundleMappingResult> {
    const response = await client.post<OrcaBundleMappingResult>(
      `/slicer/profiles/import/orca/map`,
      preview
    );
    return response.data;
  },
};
