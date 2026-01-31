import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import { Button, Input } from '@/common/components/ui';
import { XMarkIcon, PlusIcon } from '@heroicons/react/24/solid';

interface TagOption {
  id: string;
  name: string;
  color?: string;
  description?: string;
}

interface TagSelectorProps {
  selectedTags: TagOption[];
  onTagsChange: (tags: TagOption[]) => void;
  onSave?: (tags: TagOption[]) => Promise<void>;
  isSaving?: boolean;
  isLoading?: boolean;
  placeholder?: string;
}

// Generate random color outside of render (called once per tag creation)
function generateRandomColor(): string {
  return '#' + Math.floor(Math.random()*16777215).toString(16).padStart(6, '0');
}

/**
 * Reusable tag selector component for tagging any object (models, gcode files, etc.)
 * Displays available tags, allows selection/deselection, and provides save callback
 */
export const TagSelector: React.FC<TagSelectorProps> = ({
  selectedTags,
  onTagsChange,
  onSave,
  isSaving = false,
  isLoading = false,
  placeholder = 'Search tags...'
}) => {
  const [searchTerm, setSearchTerm] = useState('');
  const queryClient = useQueryClient();

  // Create new tag mutation
  const createTagMutation = useMutation({
    mutationFn: async (tagName: string) => {
      return await apiClient.createTag(
        tagName,
        generateRandomColor(),
        ''
      );
    },
    onSuccess: (newTag) => {
      queryClient.invalidateQueries({ queryKey: ['all-tags'] });
      if (newTag) handleAddTag(newTag as unknown as TagOption);
      setSearchTerm('');
    }
  });

  // Fetch all available tags
  const { data: allTags = [], isLoading: isLoadingTags } = useQuery<TagOption[]>({
    queryKey: ['all-tags'],
    queryFn: async () => {
      const result = await apiClient.listTags();
      return (result as unknown as TagOption[]) || [];
    },
    staleTime: 5 * 60 * 1000
  });

  // Filter tags based on search
  const filteredTags = (Array.isArray(allTags) ? allTags : []).filter(
    (tag: TagOption) =>
      tag.name.toLowerCase().includes(searchTerm.toLowerCase()) &&
      !selectedTags.some(st => st.id === tag.id)
  );

  const handleAddTag = (tag: TagOption) => {
    onTagsChange([...selectedTags, tag]);
    setSearchTerm('');
  };

  const handleRemoveTag = (tagId: string) => {
    onTagsChange(selectedTags.filter(t => t.id !== tagId));
  };

  const handleSave = async () => {
    if (onSave) {
      await onSave(selectedTags);
    }
  };

  return (
    <div className="space-y-3">
      {/* Selected Tags */}
      <div className="flex flex-wrap gap-2">
        {selectedTags.map(tag => (
          <div
            key={tag.id}
            className="inline-flex items-center gap-2 px-3 py-1 rounded-full text-sm font-medium text-white"
            style={{ backgroundColor: tag.color || 'var(--pf-accent)' }}
          >
            {tag.name}
            <Button
              onClick={() => handleRemoveTag(tag.id)}
              className="hover:opacity-80 transition-opacity"
              title={`Remove ${tag.name} tag`}
              variant="subtle"
              size="sm"
              iconCenter={<XMarkIcon className="w-4 h-4" />}
            />
          </div>
        ))}
      </div>

      {/* Tag Search & Add */}
      <div className="space-y-2">
        <div className="relative">
          <Input
            type="text"
            placeholder={placeholder}
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            disabled={isLoadingTags || isSaving}
            className="pr-10"
          />
          {searchTerm && !isLoadingTags && (
            <Button
              onClick={() => setSearchTerm('')}
              className="absolute right-3 top-1/2 -translate-y-1/2 text-pf-text-tertiary hover:text-pf-text-secondary"
              variant="subtle"
              size="sm"
            >
              <XMarkIcon className="w-4 h-4" />
            </Button>
          )}
        </div>

        {/* Tag Suggestions Dropdown */}
        {searchTerm && (
          <div className="bg-pf-bg-1 border border-pf-border rounded-lg max-h-48 overflow-y-auto">
            {isLoadingTags ? (
              <div className="p-3 text-sm text-pf-text-tertiary text-center">
                Loading tags...
              </div>
            ) : filteredTags.length > 0 ? (
              <div className="divide-y divide-pf-border">
                {filteredTags.map(tag => (
                  <Button
                    key={tag.id}
                    onClick={() => handleAddTag(tag)}
                    className="w-full text-left px-3 py-2 hover:bg-pf-bg-2 transition-colors flex items-center gap-2"
                    variant="subtle"
                  >
                    {tag.color && (
                      <div
                        className="w-3 h-3 rounded-full flex-shrink-0"
                        style={{ backgroundColor: tag.color }}
                      />
                    )}
                    <span className="text-sm text-pf-text-primary">{tag.name}</span>
                    {tag.description && (
                      <span className="text-xs text-pf-text-tertiary ml-auto">
                        {tag.description}
                      </span>
                    )}
                  </Button>
                ))}
              </div>
            ) : (
              <Button
                onClick={() => createTagMutation.mutate(searchTerm)}
                disabled={createTagMutation.isPending}
                className="w-full text-left px-3 py-2 hover:bg-pf-bg-2 transition-colors flex items-center gap-2"
                variant="subtle"
                iconLeft={<PlusIcon className="w-4 h-4 text-pf-accent" />}
              >
                <span className="text-sm text-pf-text-primary">
                  {createTagMutation.isPending ? 'Creating...' : `Create tag "${searchTerm}"`}
                </span>
              </Button>
            )}
          </div>
        )}
      </div>

      {/* Save Button */}
      {onSave && (
        <Button
          onClick={handleSave}
          disabled={isSaving || isLoading}
          variant="primary"
          size="sm"
          className="w-full"
        >
          {isSaving ? 'Saving...' : 'Save Tags'}
        </Button>
      )}
    </div>
  );
};
