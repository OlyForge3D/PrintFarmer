import axios from 'axios';
import {
  OrcaBundlePreview,
  ImportOrcaBundleRequest,
  ImportOrcaBundleResult,
  OrcaBundleMappingResult,
  ExportOrcaBundleRequest,
} from './orcaProfiles';

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || 'http://localhost:5245';

export const orcaProfilesService = {
  /**
   * Preview an OrcaSlicer bundle without importing.
   * Returns structured preview of all detected presets.
   */
  async previewBundle(bundleJson: string): Promise<OrcaBundlePreview> {
    const response = await axios.post<OrcaBundlePreview>(
      `${API_BASE_URL}/api/slicer/profiles/import/orca/preview`,
      { bundleJson }
    );
    return response.data;
  },

  /**
   * Import selected presets from an OrcaSlicer bundle.
   * Returns import result with counts and any errors/warnings.
   */
  async importBundle(request: ImportOrcaBundleRequest): Promise<ImportOrcaBundleResult> {
    const response = await axios.post<ImportOrcaBundleResult>(
      `${API_BASE_URL}/api/slicer/profiles/import/orca`,
      request
    );
    return response.data;
  },

  /**
   * Export PrintFarmer profiles to OrcaSlicer config bundle format.
   * Returns a valid OrcaSlicer JSON bundle string.
   */
  async exportBundle(request?: ExportOrcaBundleRequest): Promise<string> {
    const response = await axios.post<string>(
      `${API_BASE_URL}/api/slicer/profiles/export/orca`,
      request || {}
    );
    return response.data;
  },

  /**
   * Get mapping results for bundle presets (matches to catalog entities).
   */
  async mapBundlePresets(preview: OrcaBundlePreview): Promise<OrcaBundleMappingResult> {
    const response = await axios.post<OrcaBundleMappingResult>(
      `${API_BASE_URL}/api/slicer/profiles/import/orca/map`,
      preview
    );
    return response.data;
  },
};
