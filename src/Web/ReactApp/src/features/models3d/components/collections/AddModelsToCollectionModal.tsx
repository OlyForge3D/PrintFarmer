import { useState } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Checkbox, EmptyState } from '@/common/components/ui';
import { FolderPlusIcon, EarthIcon } from '@/common/components/icons/MdiIcons';
import { useModelCollections, useAddModelsToCollection } from '@/features/models3d/hooks/useCollections';

export interface AddModelsToCollectionModalProps {
  isOpen: boolean;
  /** The models the current selection/action applies to. */
  modelIds: string[];
  onClose: () => void;
  /** Opens the "new collection" form; the resulting collection can then be checked here. */
  onCreateNew: () => void;
}

/**
 * Lets the user add a batch of already-selected models to one or more collections in a
 * single action (#846 "efficient multi-model membership actions"). There is no batch
 * membership REST endpoint outside the desktop sync protocol, so submitting fans out to
 * parallel per-model/per-collection requests behind one loading state and one toast
 * per collection (see useAddModelsToCollection).
 *
 * Callers must remount this component (via a changing `key`) each time it is opened so the
 * checkbox selection starts empty, avoiding a reset-on-open effect.
 */
export function AddModelsToCollectionModal({ isOpen, modelIds, onClose, onCreateNew }: AddModelsToCollectionModalProps) {
  const { data: collections = [], isLoading } = useModelCollections();
  const addModels = useAddModelsToCollection();
  const [checkedIds, setCheckedIds] = useState<string[]>([]);

  const toggle = (id: string, checked: boolean) => {
    setCheckedIds((prev) => (checked ? [...prev, id] : prev.filter((existing) => existing !== id)));
  };

  const handleSubmit = async () => {
    await Promise.all(checkedIds.map((collectionId) => addModels.mutateAsync({ collectionId, modelIds })));
    onClose();
  };

  const isSaving = addModels.isPending;
  const modelCountLabel = `${modelIds.length} model${modelIds.length === 1 ? '' : 's'}`;

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={`Add ${modelCountLabel} to Collections`}
      isDisabled={isSaving}
      footer={
        <div className="flex justify-end gap-3 w-full">
          <Button variant="secondary" onClick={onClose} disabled={isSaving}>
            Cancel
          </Button>
          <Button
            variant="primary"
            onClick={handleSubmit}
            loading={isSaving}
            disabled={isSaving || checkedIds.length === 0}
          >
            Add to {checkedIds.length} collection{checkedIds.length === 1 ? '' : 's'}
          </Button>
        </div>
      }
    >
      <div className="space-y-4">
        <div className="flex items-center justify-between">
          <p className="text-sm text-pf-text-secondary">
            Choose which collections should include {modelCountLabel}.
          </p>
          <Button
            type="button"
            variant="secondary"
            size="sm"
            iconLeft={<FolderPlusIcon className="w-4 h-4" />}
            onClick={onCreateNew}
            disabled={isSaving}
          >
            New collection
          </Button>
        </div>

        {isLoading ? (
          <p className="text-sm text-pf-text-tertiary" role="status">
            Loading collections…
          </p>
        ) : collections.length === 0 ? (
          <EmptyState
            title="No collections yet"
            description="Create a collection to start organizing your models."
            action={
              <Button variant="primary" size="sm" onClick={onCreateNew} iconLeft={<FolderPlusIcon className="w-4 h-4" />}>
                New collection
              </Button>
            }
          />
        ) : (
          <ul className="max-h-72 overflow-y-auto rounded-lg border border-pf-border divide-y divide-pf-border">
            {collections.map((collection) => {
              const checkboxId = `collection-checkbox-${collection.id}`;
              return (
                <li key={collection.id}>
                  <label
                    htmlFor={checkboxId}
                    className="flex items-center gap-3 p-3 cursor-pointer hover:bg-pf-bg-2 transition-colors"
                  >
                    <Checkbox
                      id={checkboxId}
                      checked={checkedIds.includes(collection.id)}
                      onChange={(e) => toggle(collection.id, e.target.checked)}
                      disabled={isSaving}
                    />
                    <div className="flex-1 min-w-0">
                      <div className="flex items-center gap-2">
                        <span className="font-medium text-pf-text-primary truncate">{collection.name}</span>
                        {collection.isShared && (
                          <span className="inline-flex items-center gap-1 text-xs text-pf-text-tertiary" title="Shared with everyone">
                            <EarthIcon className="w-3.5 h-3.5" />
                            Shared
                          </span>
                        )}
                      </div>
                      <div className="text-xs text-pf-text-tertiary">
                        {collection.memberCount} model{collection.memberCount === 1 ? '' : 's'}
                      </div>
                    </div>
                  </label>
                </li>
              );
            })}
          </ul>
        )}
      </div>
    </Modal>
  );
}
