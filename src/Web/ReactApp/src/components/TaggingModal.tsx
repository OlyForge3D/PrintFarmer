import React, { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Button, Alert } from '@/common/components/ui';
import { TagSelector } from './TagSelector';
import { tagService, type TagDto as TagOption } from '@/services/tagService';
import { Modal } from '@/common/components/modals/Modal';
import { apiClient } from '@/services/api';
import { printerTagsFleetQueryKey } from '@/features/printers/hooks/usePrinterTagsFleet';

interface TaggingModalProps {
  objectId: string;
  objectType: 'Model3D' | 'GcodeFile' | 'Printer';
  initialTags?: TagOption[];
  isOpen: boolean;
  onClose: () => void;
  /**
   * True while the caller's own tag source is still resolving `initialTags`
   * for the first time (e.g. an async fleet-batched query that hasn't
   * hydrated yet). Consumers whose tags are already loaded synchronously
   * (file/model lists) can omit this.
   *
   * Some consumers (e.g. `CompactPrinterCard`) keep this modal mounted the
   * whole time and only toggle `isOpen`, rather than mounting it fresh on
   * every open. Without this flag, a card that renders before its tags have
   * hydrated would seed `selectedTags` from `[]`; saving from that state
   * would then diff against the (by-then-resolved) `initialTags` and issue
   * "remove" calls for every real tag the object already has. This prevents
   * that by blocking Save until the source has resolved at least once.
   */
  tagsLoading?: boolean;
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
  onClose,
  tagsLoading = false,
}) => {
  const [selectedTags, setSelectedTags] = useState<TagOption[]>(initialTags);
  const [error, setError] = useState<string | null>(null);
  const queryClient = useQueryClient();

  // Tracks which object id the current `selectedTags` selection was last
  // resolved for (or `null` while closed / still waiting on `tagsLoading`).
  // Some consumers keep this modal mounted across opens (`isOpen` merely
  // toggles visibility) instead of mounting a fresh instance per open, so
  // `useState(initialTags)`'s initial value only ever applies once, on the
  // very first mount — often before an async tag source has hydrated.
  //
  // Re-seeding `selectedTags` here — during render, comparing against the
  // previous key, per React's documented "adjusting state when a prop
  // changes" pattern (https://react.dev/learn/you-might-not-need-an-effect),
  // rather than inside a `useEffect` — fixes three things at once: (1) late
  // hydration — once the source resolves while still closed or right as it
  // opens, the very next render that's open+resolved reads the real tags
  // instead of the stale `[]` the state started with, with no stale frame in
  // between; (2) reopen after an abandoned edit — Cancel (or simply closing)
  // doesn't persist the in-progress selection, so reopening always starts
  // from the true current tags rather than whatever was left over from a
  // discarded session; (3) a different object being tagged through the same
  // mounted instance.
  const resolvedSelectionKey = isOpen && !tagsLoading ? objectId : null;
  const [lastResolvedSelectionKey, setLastResolvedSelectionKey] = useState<string | null>(null);
  if (resolvedSelectionKey !== lastResolvedSelectionKey) {
    setLastResolvedSelectionKey(resolvedSelectionKey);
    if (resolvedSelectionKey !== null) {
      setSelectedTags(initialTags);
    }
  }

  // Mutation for saving tags
  const saveMutation = useMutation({
    mutationFn: async (tags: TagOption[]) => {
      const currentTags = initialTags;
      
      // Tags to remove: in current but not in selected
      const toRemove = currentTags.filter(
        ct => !tags.some(st => st.id === ct.id)
      );
      
      // Tags to add: in selected but not in current
      const toAdd = tags.filter(
        st => !currentTags.some(ct => ct.id === st.id)
      );

      if (objectType === 'Printer') {
        // Use apiClient directly for Printer — tagService only handles model/gcode
        for (const tag of toRemove) {
          await apiClient.removeTagFromObject(objectId, tag.id, 'Printer');
        }
        for (const tag of toAdd) {
          await apiClient.assignTagToObject(objectId, tag.id, 'Printer');
        }
      } else {
        const fileType = objectType === 'Model3D' ? 'model' : 'gcode';
        for (const tag of toRemove) {
          await tagService.removeTag(objectId, tag.id, fileType as 'model' | 'gcode');
        }
        for (const tag of toAdd) {
          await tagService.assignTag(objectId, tag.id, fileType as 'model' | 'gcode');
        }
      }
    },
    onSuccess: () => {
      // Invalidate relevant caches
      queryClient.invalidateQueries({ queryKey: ['file-browser'] });
      queryClient.invalidateQueries({ queryKey: ['gcode-tags'] });
      queryClient.invalidateQueries({ queryKey: ['model-tags'] });
      queryClient.invalidateQueries({ queryKey: ['admin-all-tags'] });
      if (objectType === 'Printer') {
        // Compatibility key (#1146 item 1): kept for any lingering
        // single-object consumer of the pre-fleet per-card tag query.
        queryClient.invalidateQueries({ queryKey: ['printer-tags', objectId] });
        // Fleet key: refetches the batched `GET /api/tags/objects` read every
        // compact printer card now shares, so an edit shows up immediately
        // instead of waiting out the fleet query's staleTime.
        queryClient.invalidateQueries({ queryKey: printerTagsFleetQueryKey });
      }
      setError(null);
      onClose();
    },
    onError: (err) => {
      setError(err instanceof Error ? err.message : 'Failed to save tags');
    }
  });

  if (!isOpen) return null;

  const isSaveMutating = saveMutation.isPending;
  // Block Save until the caller's tag source has resolved at least once —
  // saving from a `selectedTags` seeded off an unresolved (possibly empty)
  // `initialTags` is exactly the data-loss path this component now guards
  // against via the resync logic above; this is the belt-and-suspenders
  // backstop for the narrow window before that logic has anything to sync.
  const isSaveDisabled = isSaveMutating || tagsLoading;

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={`Tag ${objectType === 'GcodeFile' ? 'G-Code File' : objectType === 'Model3D' ? 'Model File' : objectType}`}
      isDisabled={isSaveMutating}
      footer={
        <div className="flex gap-2 w-full">
          <Button
            onClick={onClose}
            disabled={isSaveMutating}
            variant="secondary"
            className="flex-1"
          >
            Cancel
          </Button>
          <Button
            onClick={() => saveMutation.mutate(selectedTags)}
            disabled={isSaveDisabled}
            variant="primary"
            className="flex-1"
          >
            {isSaveMutating ? 'Saving...' : tagsLoading ? 'Loading tags…' : 'Save Tags'}
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
        isSaving={isSaveMutating}
        placeholder={`Search tags for this ${objectType === 'GcodeFile' ? 'gcode file' : objectType.toLowerCase()}...`}
      />
    </Modal>
  );
};
