import { getApiBaseUrl, getAuthHeaders } from '@/common/utils/apiUrlHelpers';

export interface TagOption {
  id: string;
  name: string;
  color?: string;
  description?: string;
  usageCount?: number;
}

export const gcodeFileTagService = {
  /**
   * Get all tags for a specific gcode file
   */
  getTags: async (gcodeFileId: string): Promise<TagOption[]> => {
    const response = await fetch(
      `${getApiBaseUrl()}/gcode-files/${gcodeFileId}/tags`,
      { headers: getAuthHeaders() }
    );
    if (!response.ok) throw new Error('Failed to fetch gcode file tags');
    return response.json();
  },

  /**
   * Add a tag to a gcode file
   */
  addTag: async (gcodeFileId: string, tagId: string): Promise<void> => {
    const response = await fetch(
      `${getApiBaseUrl()}/gcode-files/${gcodeFileId}/tags/${tagId}`,
      {
        method: 'POST',
        headers: getAuthHeaders()
      }
    );
    if (!response.ok) throw new Error('Failed to add tag to gcode file');
  },

  /**
   * Remove a tag from a gcode file
   */
  removeTag: async (gcodeFileId: string, tagId: string): Promise<void> => {
    const response = await fetch(
      `${getApiBaseUrl()}/gcode-files/${gcodeFileId}/tags/${tagId}`,
      {
        method: 'DELETE',
        headers: getAuthHeaders()
      }
    );
    if (!response.ok) throw new Error('Failed to remove tag from gcode file');
  },

  /**
   * Batch add multiple tags to a gcode file
   */
  addTags: async (gcodeFileId: string, tagIds: string[]): Promise<void> => {
    for (const tagId of tagIds) {
      await gcodeFileTagService.addTag(gcodeFileId, tagId);
    }
  },

  /**
   * Get tag analytics for all gcode files
   */
  getAnalytics: async () => {
    const response = await fetch(
      `${getApiBaseUrl()}/gcode-files/tags/analytics`,
      { headers: getAuthHeaders() }
    );
    if (!response.ok) throw new Error('Failed to fetch gcode file tag analytics');
    return response.json();
  }
};
