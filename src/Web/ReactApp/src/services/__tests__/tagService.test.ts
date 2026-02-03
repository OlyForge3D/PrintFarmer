import { describe, it, expect, vi, beforeEach } from 'vitest';
import { tagService, TagDto, TagSuggestionDto } from '../tagService';
import { apiClient } from '../api';

// Mock the api client
vi.mock('../api', () => ({
  apiClient: {
    listTags: vi.fn(),
    searchTags: vi.fn(),
    getPopularTags: vi.fn(),
    createTag: vi.fn(),
    deleteTag: vi.fn(),
    getTagAnalytics: vi.fn(),
  }
}));

describe('tagService', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  describe('listTags', () => {
    it('should list all tags', async () => {
      const mockTags: TagDto[] = [
        { id: '1', name: 'prototype', color: '#ff0000' },
        { id: '2', name: 'functional', color: '#00ff00' },
      ];

      vi.mocked(apiClient.listTags).mockResolvedValue(mockTags as never);

      const result = await tagService.listTags();

      expect(result).toEqual(mockTags);
      expect(apiClient.listTags).toHaveBeenCalled();
    });

    it('should handle errors and return empty array', async () => {
      const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      vi.mocked(apiClient.listTags).mockRejectedValue(new Error('Network error'));

      const result = await tagService.listTags();

      expect(result).toEqual([]);
      expect(consoleSpy).toHaveBeenCalled();
      consoleSpy.mockRestore();
    });
  });

  describe('searchTags', () => {
    it('should debounce search calls', async () => {
      const callback = vi.fn();
      const mockResults: TagSuggestionDto[] = [
        { id: '1', name: 'prototype', usageCount: 5 },
      ];

      vi.mocked(apiClient.searchTags).mockResolvedValue(mockResults as never);

      tagService.searchTags('proto', callback);
      tagService.searchTags('protot', callback);
      tagService.searchTags('prototy', callback);

      // Advance timers to trigger debounced call
      await vi.advanceTimersByTimeAsync(300);

      // Should only call API once due to debouncing
      expect(apiClient.searchTags).toHaveBeenCalledTimes(1);
      expect(apiClient.searchTags).toHaveBeenCalledWith('prototy');
    });

    it('should return empty results for empty query', () => {
      const callback = vi.fn();

      tagService.searchTags('', callback);

      expect(callback).toHaveBeenCalledWith([]);
      expect(apiClient.searchTags).not.toHaveBeenCalled();
    });

    it('should handle search errors', async () => {
      const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      const callback = vi.fn();
      
      vi.mocked(apiClient.searchTags).mockRejectedValue(new Error('Search failed'));

      tagService.searchTags('test', callback);
      
      await vi.advanceTimersByTimeAsync(300);

      expect(callback).toHaveBeenCalledWith([]);
      expect(consoleSpy).toHaveBeenCalled();
      consoleSpy.mockRestore();
    });
  });

  describe('getPopularTags', () => {
    it('should get popular tags', async () => {
      const mockTags: TagSuggestionDto[] = [
        { id: '1', name: 'prototype', usageCount: 100 },
        { id: '2', name: 'functional', usageCount: 50 },
      ];

      vi.mocked(apiClient.getPopularTags).mockResolvedValue(mockTags as never);

      const result = await tagService.getPopularTags();

      expect(result.length).toBe(2);
      expect(apiClient.getPopularTags).toHaveBeenCalled();
    });
  });

  describe('createTag', () => {
    it('should create a new tag', async () => {
      const newTag: TagDto = {
        id: '1',
        name: 'new-tag',
        color: '#0000ff'
      };

      vi.mocked(apiClient.createTag).mockResolvedValue(newTag as never);

      const result = await tagService.createTag('new-tag', '#0000ff');

      expect(result).toEqual(newTag);
      expect(apiClient.createTag).toHaveBeenCalledWith('new-tag', '#0000ff', undefined);
    });

    it('should create a tag with description', async () => {
      const newTag: TagDto = {
        id: '1',
        name: 'new-tag',
        color: '#0000ff',
        description: 'Test description'
      };

      vi.mocked(apiClient.createTag).mockResolvedValue(newTag as never);

      const result = await tagService.createTag('new-tag', '#0000ff', 'Test description');

      expect(result).toEqual(newTag);
      expect(apiClient.createTag).toHaveBeenCalledWith('new-tag', '#0000ff', 'Test description');
    });
  });

  describe('deleteTag', () => {
    it('should delete a tag', async () => {
      vi.mocked(apiClient.deleteTag).mockResolvedValue(undefined as never);

      const result = await tagService.deleteTag('1');

      expect(result).toBe(true);
      expect(apiClient.deleteTag).toHaveBeenCalledWith('1');
    });

    it('should handle deletion errors', async () => {
      const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      vi.mocked(apiClient.deleteTag).mockRejectedValue(new Error('Delete failed'));

      const result = await tagService.deleteTag('1');

      expect(result).toBe(false);
      expect(consoleSpy).toHaveBeenCalled();
      consoleSpy.mockRestore();
    });
  });

  describe('getAnalytics', () => {
    it('should get tag analytics', async () => {
      const mockAnalytics = {
        totalTags: 50,
        tagsInUse: 30,
        unusedTags: 20,
        totalModelTagAssociations: 150,
        averageTagsPerModel: 3,
        topTags: [],
        unusedTagsList: []
      };

      vi.mocked(apiClient.getTagAnalytics).mockResolvedValue(mockAnalytics as never);

      const result = await tagService.getAnalytics();

      expect(result).toEqual(mockAnalytics);
      expect(apiClient.getTagAnalytics).toHaveBeenCalled();
    });

    it('should handle analytics errors', async () => {
      const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
      vi.mocked(apiClient.getTagAnalytics).mockRejectedValue(new Error('Failed'));

      const result = await tagService.getAnalytics();

      expect(result).toBeNull();
      expect(consoleSpy).toHaveBeenCalled();
      consoleSpy.mockRestore();
    });
  });
});

