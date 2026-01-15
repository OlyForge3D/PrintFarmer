import axios, { AxiosError } from 'axios';

/**
 * Tag data transfer object
 */
export interface TagDto {
  id: string;
  name: string;
  color?: string;
  description?: string;
}

/**
 * Tag suggestion with usage count
 */
export interface TagSuggestionDto {
  id: string;
  name: string;
  usageCount: number;
  color?: string;
}

/**
 * Tag analytics data
 */
export interface TagAnalyticsDto {
  totalTags: number;
  totalAssignments: number;
  averageTagsPerModel: number;
  mostUsedTags: TagSuggestionDto[];
  leastUsedTags: TagSuggestionDto[];
}

/**
 * API response wrapper for lists
 */
/**
 * Tag service for API integration
 * Handles all tag-related API calls with error handling and caching
 */
class TagService {
  private readonly baseUrl = '/api/tags';
  private readonly debounceDelay = 300;
  private searchTimeouts: Map<string, NodeJS.Timeout> = new Map();
  private tagsCache: Map<string, TagDto> = new Map();
  private popularTagsCache: TagSuggestionDto[] = [];
  private cacheExpiry: number = 5 * 60 * 1000; // 5 minutes
  private lastCacheTime: number = 0;

  /**
   * Get all tags
   */
  async listTags(): Promise<TagDto[]> {
    try {
      const response = await axios.get<TagDto[]>(`${this.baseUrl}/tags`);
      const tags = response.data || [];
      
      // Update cache
      tags.forEach(tag => {
        this.tagsCache.set(tag.id, tag);
      });
      
      return tags;
    } catch (error) {
      this.handleError('Failed to list tags', error);
      return [];
    }
  }

  /**
   * Search tags by query with debouncing
   */
  searchTags(query: string, callback: (results: TagSuggestionDto[]) => void): void {
    // Clear existing timeout
    const existingTimeout = this.searchTimeouts.get('search');
    if (existingTimeout) {
      clearTimeout(existingTimeout);
    }

    if (!query || query.trim().length === 0) {
      callback([]);
      return;
    }

    // Set new timeout for debouncing
    const timeout = setTimeout(async () => {
      try {
        const response = await axios.get<TagSuggestionDto[]>(
          `${this.baseUrl}/tags/search`,
          { params: { q: query } }
        );
        callback(response.data || []);
      } catch (error) {
        this.handleError('Failed to search tags', error);
        callback([]);
      }
    }, this.debounceDelay);

    this.searchTimeouts.set('search', timeout);
  }

  /**
   * Get most popular tags
   */
  async getPopularTags(count: number = 10): Promise<TagSuggestionDto[]> {
    try {
      // Check cache validity
      const now = Date.now();
      if (this.popularTagsCache.length > 0 && (now - this.lastCacheTime) < this.cacheExpiry) {
        return this.popularTagsCache.slice(0, count);
      }

      const response = await axios.get<TagSuggestionDto[]>(
        `${this.baseUrl}/tags/popular`,
        { params: { count: count * 2 } } // Fetch extra in case some are filtered
      );

      this.popularTagsCache = response.data || [];
      this.lastCacheTime = now;

      return this.popularTagsCache.slice(0, count);
    } catch (error) {
      this.handleError('Failed to get popular tags', error);
      return this.popularTagsCache.slice(0, count);
    }
  }

  /**
   * Get tag analytics
   */
  async getAnalytics(): Promise<TagAnalyticsDto | null> {
    try {
      const response = await axios.get<TagAnalyticsDto>(`${this.baseUrl}/tags/analytics`);
      return response.data || null;
    } catch (error) {
      this.handleError('Failed to get tag analytics', error);
      return null;
    }
  }

  /**
   * Create a new tag
   */
  async createTag(name: string, color?: string, description?: string): Promise<TagDto | null> {
    try {
      const response = await axios.post<TagDto>(`${this.baseUrl}/tags`, {
        name,
        color,
        description,
      });

      const tag = response.data;
      if (tag) {
        this.tagsCache.set(tag.id, tag);
      }

      return tag || null;
    } catch (error) {
      this.handleError('Failed to create tag', error);
      return null;
    }
  }

  /**
   * Delete a tag
   */
  async deleteTag(tagId: string): Promise<boolean> {
    try {
      await axios.delete(`${this.baseUrl}/tags/${tagId}`);
      this.tagsCache.delete(tagId);
      return true;
    } catch (error) {
      this.handleError('Failed to delete tag', error);
      return false;
    }
  }

  /**
   * Get tag by ID from cache or API
   */
  async getTagById(tagId: string): Promise<TagDto | null> {
    // Check cache first
    if (this.tagsCache.has(tagId)) {
      return this.tagsCache.get(tagId) || null;
    }

    try {
      const response = await axios.get<TagDto>(`${this.baseUrl}/tags/${tagId}`);
      const tag = response.data;

      if (tag) {
        this.tagsCache.set(tag.id, tag);
      }

      return tag || null;
    } catch (error) {
      this.handleError(`Failed to get tag ${tagId}`, error);
      return null;
    }
  }

  /**
   * Filter models by tags (all tags required)
   */
  async filterModelsWithAllTags(tagIds: string[]): Promise<string[]> {
    if (tagIds.length === 0) {
      return [];
    }

    try {
      const response = await axios.get<string[]>(
        `${this.baseUrl}/models/filter/all-tags`,
        { params: { tags: tagIds.join(',') } }
      );

      return response.data || [];
    } catch (error) {
      this.handleError('Failed to filter models with all tags', error);
      return [];
    }
  }

  /**
   * Filter models by tags (any tag matches)
   */
  async filterModelsWithAnyTag(tagIds: string[]): Promise<string[]> {
    if (tagIds.length === 0) {
      return [];
    }

    try {
      const response = await axios.get<string[]>(
        `${this.baseUrl}/models/filter/any-tags`,
        { params: { tags: tagIds.join(',') } }
      );

      return response.data || [];
    } catch (error) {
      this.handleError('Failed to filter models with any tag', error);
      return [];
    }
  }

  /**
   * Complex filtering with include/exclude
   */
  async filterModelsComplex(
    includeAllTagIds?: string[],
    includeAnyTagIds?: string[],
    excludeTagIds?: string[]
  ): Promise<string[]> {
    try {
      const params: Record<string, string> = {};
      
      if (includeAllTagIds && includeAllTagIds.length > 0) {
        params.includeAll = includeAllTagIds.join(',');
      }
      
      if (includeAnyTagIds && includeAnyTagIds.length > 0) {
        params.includeAny = includeAnyTagIds.join(',');
      }
      
      if (excludeTagIds && excludeTagIds.length > 0) {
        params.exclude = excludeTagIds.join(',');
      }

      const response = await axios.get<string[]>(
        `${this.baseUrl}/models/filter`,
        { params }
      );

      return response.data || [];
    } catch (error) {
      this.handleError('Failed to filter models with complex criteria', error);
      return [];
    }
  }

  /**
   * Clear caches
   */
  clearCache(): void {
    this.tagsCache.clear();
    this.popularTagsCache = [];
    this.lastCacheTime = 0;
  }

  /**
   * Assign a tag to an object (model, gcode file, etc.)
   * @param objectId - The ID of the object to tag
   * @param tagId - The tag ID to assign
   * @param objectType - Type of object ('model' or 'gcode')
   */
  async assignTag(objectId: string, tagId: string, objectType: 'model' | 'gcode' = 'model'): Promise<void> {
    try {
      const endpoint = objectType === 'gcode' ? 'assign-to-gcode' : 'assign-to-model';
      await axios.post(
        `${this.baseUrl}/${endpoint}/${objectId}/${tagId}`
      );
    } catch (error) {
      this.handleError(`Failed to assign tag to ${objectType}`, error);
      throw error;
    }
  }

  /**
   * Remove a tag from an object (model, gcode file, etc.)
   * @param objectId - The ID of the object to untag
   * @param tagId - The tag ID to remove
   * @param objectType - Type of object ('model' or 'gcode')
   */
  async removeTag(objectId: string, tagId: string, objectType: 'model' | 'gcode' = 'model'): Promise<void> {
    try {
      const endpoint = objectType === 'gcode' ? 'remove-from-gcode' : 'remove-from-model';
      await axios.delete(
        `${this.baseUrl}/${endpoint}/${objectId}/${tagId}`
      );
    } catch (error) {
      this.handleError(`Failed to remove tag from ${objectType}`, error);
      throw error;
    }
  }

  /**
   * Get tags for a gcode file
   * Note: Only gcode files have a dedicated endpoint. Model tags come from the model data itself.
   */
  async getGcodeFileTags(gcodeFileId: string): Promise<TagDto[]> {
    try {
      const response = await axios.get<TagDto[]>(
        `${this.baseUrl}/gcode-file/${gcodeFileId}`
      );
      return response.data || [];
    } catch (error) {
      this.handleError('Failed to fetch gcode file tags', error);
      return [];
    }
  }

  /**
   * Batch assign multiple tags to an object
   */
  async assignTags(objectId: string, tagIds: string[], objectType: 'model' | 'gcode' = 'model'): Promise<void> {
    try {
      for (const tagId of tagIds) {
        await this.assignTag(objectId, tagId, objectType);
      }
    } catch (error) {
      this.handleError(`Failed to batch assign tags to ${objectType}`, error);
      throw error;
    }
  }

  /**
   * Handle API errors
   */
  private handleError(message: string, error: unknown): void {
    if (axios.isAxiosError(error)) {
      const axiosError = error as AxiosError;
      console.error(
        `${message}: ${axiosError.response?.status} ${axiosError.response?.statusText}`,
        axiosError.response?.data
      );
    } else {
      console.error(message, error);
    }
  }
}

// Export singleton instance
export const tagService = new TagService();

export default tagService;
