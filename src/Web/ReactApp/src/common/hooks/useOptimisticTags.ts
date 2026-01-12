import { useOptimistic } from 'react';
import { fileService } from '@/services/fileService';
import { Tag } from '@/types/api';

interface OptimisticTagAction {
  type: 'add' | 'remove';
  tag?: Tag;
  tagId?: string;
}

export function useOptimisticTags(fileId: string, initialTags: Tag[]) {
  const [optimisticTags, updateOptimisticTags] = useOptimistic(
    initialTags,
    (state: Tag[], action: OptimisticTagAction) => {
      if (action.type === 'add' && action.tag) {
        // Check if tag already exists to avoid duplicates
        if (state.some(t => t.id === action.tag!.id)) {
          return state;
        }
        return [...state, action.tag];
      } else if (action.type === 'remove' && action.tagId) {
        return state.filter(t => t.id !== action.tagId);
      }
      return state;
    }
  );

  const addTag = async (tag: Tag) => {
    updateOptimisticTags({ type: 'add', tag });
    try {
      await fileService.addTagToModel(fileId, tag.id);
    } catch (error) {
      // Automatically reverts on error due to useOptimistic
      console.error('Failed to add tag:', error);
      throw error;
    }
  };

  const removeTag = async (tagId: string) => {
    updateOptimisticTags({ type: 'remove', tagId });
    try {
      await fileService.removeTagFromModel(fileId, tagId);
    } catch (error) {
      // Automatically reverts on error due to useOptimistic
      console.error('Failed to remove tag:', error);
      throw error;
    }
  };

  return {
    tags: optimisticTags,
    addTag,
    removeTag,
  };
}
