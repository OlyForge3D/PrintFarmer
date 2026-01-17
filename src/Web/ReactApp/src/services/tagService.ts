import { apiClient } from '@/services/api';

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
  tagsInUse: number;
  unusedTags: number;
  totalModelTagAssociations: number;
  averageTagsPerModel: number;
  topTags: TagStatDto[];
  unusedTagsList: TagStatDto[];
}

/**
 * Individual tag statistics
 */
export interface TagStatDto {
  id: string;
  name: string;
  modelCount: number;
  createdAt: string;
  lastUsedAt?: string | null;
}

/**
 * API response wrapper for lists
 */
/**
 * Tag service for API integration
 * Handles all tag-related API calls with error handling and caching
 * Delegated to apiClient singleton which handles authentication automatically
 */
class TagService {
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
      const tags = await apiClient.listTags();
      
      // Update cache
      const typedTags = tags as unknown as TagDto[];
      typedTags.forEach((tag: TagDto) => {
        this.tagsCache.set((tag as unknown as Record<string, unknown>).id as string, tag);
      });
      
      return typedTags;
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
        const results = await apiClient.searchTags(query);
        callback((results as unknown as TagSuggestionDto[]) || []);
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

      const popular = await apiClient.getPopularTags(count * 2);
      this.popularTagsCache = popular as unknown as TagSuggestionDto[];
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
      const analytics = await apiClient.getTagAnalytics();
      return (analytics as unknown as TagAnalyticsDto) || null;
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
      const tag = await apiClient.createTag(name, color, description);
      if (tag) {
        const typedTag = tag as unknown as TagDto;
        this.tagsCache.set((typedTag as unknown as Record<string, unknown>).id as string, typedTag);
      }
      return (tag as unknown as TagDto) || null;
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
      await apiClient.deleteTag(tagId);
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
        const tag = await apiClient.getTagById(tagId);
        if (tag) {
          const typedTag = tag as unknown as TagDto;
          this.tagsCache.set(((typedTag as unknown as Record<string, unknown>).id as string), typedTag);
        }
        return (tag as unknown as TagDto) || null;
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
      return await apiClient.filterModelsWithAllTags(tagIds);
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
      return await apiClient.filterModelsWithAnyTag(tagIds);
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
      return await apiClient.filterModelsComplex(includeAllTagIds, includeAnyTagIds, excludeTagIds);
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
      const type = objectType === 'gcode' ? 'GcodeFile' : 'Model3D';
      await apiClient.assignTagToObject(objectId, tagId, type);
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
      const type = objectType === 'gcode' ? 'GcodeFile' : 'Model3D';
      await apiClient.removeTagFromObject(objectId, tagId, type);
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
      const fileTags = await apiClient.getGcodeFileTags(gcodeFileId);
      return (fileTags as unknown as TagDto[]) || [];
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
    if (error instanceof Error) {
      console.error(`${message}: ${error.message}`, error);
    } else {
      console.error(message, error);
    }
  }
}

// Export singleton instance
export const tagService = new TagService();

export default tagService;
