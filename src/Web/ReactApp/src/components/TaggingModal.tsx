import React, { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Button, Alert } from '@/common/components/ui';
import { XMarkIcon } from '@heroicons/react/24/solid';
import { TagSelector } from './TagSelector';
import { gcodeFileTagService, TagOption } from '@/services/gcodeFileTagService';

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
      if (objectType === 'GcodeFile') {
        // Get current tags
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
          await gcodeFileTagService.removeTag(objectId, tag.id);
        }

        // Add new tags
        for (const tag of toAdd) {
          await gcodeFileTagService.addTag(objectId, tag.id);
        }
      } else if (objectType === 'Model3D') {
        // Similar logic for Model3D
        const currentTags = initialTags;
        const toRemove = currentTags.filter(
          ct => !tags.some(st => st.id === ct.id)
        );
        const toAdd = tags.filter(
          st => !currentTags.some(ct => ct.id === st.id)
        );

        // Would use model3DTagService here
        // For now, just mock the API calls
        for (const tag of toRemove) {
          await fetch(
            `/api/3d-models/${objectId}/tags/${tag.id}`,
            { method: 'DELETE' }
          );
        }
        for (const tag of toAdd) {
          await fetch(
            `/api/3d-models/${objectId}/tags/${tag.id}`,
            { method: 'POST' }
          );
        }
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['gcode-files'] });
      queryClient.invalidateQueries({ queryKey: ['models-3d'] });
      setError(null);
      onClose();
    },
    onError: (err) => {
      setError(err instanceof Error ? err.message : 'Failed to save tags');
    }
  });

  if (!isOpen) return null;

  return (
    <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50">
      <div className="bg-pf-bg-0 rounded-lg shadow-xl max-w-md w-full mx-4 p-6">
        {/* Header */}
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold text-pf-text-primary">
            Tag {objectType === 'GcodeFile' ? 'G-Code File' : objectType}
          </h2>
          <Button
            onClick={onClose}
            className="text-pf-text-tertiary hover:text-pf-text-secondary transition-colors"
            variant="subtle"
            size="sm"
          >
            <XMarkIcon className="w-5 h-5" />
          </Button>
        </div>

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
          isSaving={saveMutation.isPending}
          placeholder={`Search tags for this ${objectType === 'GcodeFile' ? 'gcode file' : objectType.toLowerCase()}...`}
        />

        {/* Actions */}
        <div className="flex gap-2 mt-6">
          <Button
            onClick={() => saveMutation.mutate(selectedTags)}
            disabled={saveMutation.isPending}
            variant="primary"
            className="flex-1"
          >
            {saveMutation.isPending ? 'Saving...' : 'Save Tags'}
          </Button>
          <Button
            onClick={onClose}
            disabled={saveMutation.isPending}
            variant="secondary"
            className="flex-1"
          >
            Cancel
          </Button>
        </div>
      </div>
    </div>
  );
};
