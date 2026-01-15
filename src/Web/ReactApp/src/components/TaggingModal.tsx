import React, { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Button, Alert } from '@/common/components/ui';
import { TagSelector } from './TagSelector';
import { tagService, type TagDto as TagOption } from '@/services/tagService';
import { Modal } from '@/common/components/modals/Modal';

interface TaggingModalProps {
  objectId: string;
  objectType: 'Model3D' | 'GcodeFile' | 'Printer';
  initialTags?: TagOption[];
  isOpen: boolean;
  onClose: () => void;
}

/**
 * Modal for tagging any taggable object (models, gcode files, printers, etc.)
 * Handles tag selection and persistence
 */
export const TaggingModal: React.FC<TaggingModalProps> = ({
  objectId,
  objectType,
  initialTags = [],
  isOpen,
  onClose
}) => {
  const [selectedTags, setSelectedTags] = useState<TagOption[]>(initialTags);
  const [error, setError] = useState<string | null>(null);
  const queryClient = useQueryClient();

  // Mutation for saving tags
  const saveMutation = useMutation({
    mutationFn: async (tags: TagOption[]) => {
      const fileType = objectType === 'Model3D' ? 'model' : 'gcode';
      const currentTags = initialTags;
      
      // Tags to remove: in current but not in selected
      const toRemove = currentTags.filter(
        ct => !tags.some(st => st.id === ct.id)
      );
      
      // Tags to add: in selected but not in current
      const toAdd = tags.filter(
        st => !currentTags.some(ct => ct.id === st.id)
      );

      // Remove old tags
      for (const tag of toRemove) {
        await tagService.removeTag(objectId, tag.id, fileType as 'model' | 'gcode');
      }

      // Add new tags
      for (const tag of toAdd) {
        await tagService.assignTag(objectId, tag.id, fileType as 'model' | 'gcode');
      }
    },
    onSuccess: () => {
      // Invalidate file browser queries (both gcode and models use 'file-browser' prefix)
      queryClient.invalidateQueries({ queryKey: ['file-browser'] });
      // Also invalidate tag queries that might be cached
      queryClient.invalidateQueries({ queryKey: ['gcode-tags'] });
      queryClient.invalidateQueries({ queryKey: ['model-tags'] });
      queryClient.invalidateQueries({ queryKey: ['admin-all-tags'] });
      setError(null);
      onClose();
    },
    onError: (err) => {
      setError(err instanceof Error ? err.message : 'Failed to save tags');
    }
  });

  if (!isOpen) return null;

  const isLoading = saveMutation.isPending;

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={`Tag ${objectType === 'GcodeFile' ? 'G-Code File' : objectType === 'Model3D' ? 'Model File' : objectType}`}
      isDisabled={isLoading}
      footer={
        <div className="flex gap-2 w-full">
          <Button
            onClick={onClose}
            disabled={isLoading}
            variant="secondary"
            className="flex-1"
          >
            Cancel
          </Button>
          <Button
            onClick={() => saveMutation.mutate(selectedTags)}
            disabled={isLoading}
            variant="primary"
            className="flex-1"
          >
            {isLoading ? 'Saving...' : 'Save Tags'}
          </Button>
        </div>
      }
    >
      {/* Error Alert */}
      {error && (
        <Alert type="error" title="Error" className="mb-4">
          {error}
        </Alert>
      )}

      {/* Tag Selector */}
      <TagSelector
        selectedTags={selectedTags}
        onTagsChange={setSelectedTags}
        isSaving={isLoading}
        placeholder={`Search tags for this ${objectType === 'GcodeFile' ? 'gcode file' : objectType.toLowerCase()}...`}
      />
    </Modal>
  );
};
