import { useId, useState } from 'react';
import type { FormEvent } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button, FormField, Input, Textarea } from '@/common/components/ui';
import type { ModelCollection } from '@/types/models';

export interface CollectionFormModalProps {
  isOpen: boolean;
  /** When provided, the modal edits (renames) this collection instead of creating a new one. */
  collection?: ModelCollection | null;
  isSaving?: boolean;
  onSubmit: (values: { name: string; description?: string }) => void;
  onClose: () => void;
}

const MAX_NAME_LENGTH = 200;
const MAX_DESCRIPTION_LENGTH = 2000;

/**
 * Create/rename modal for model collections (#843/#846). Collections CRUD uses the plain
 * REST endpoints (no server-side optimistic concurrency at this layer today), so validation
 * errors are surfaced via the standard toast + inline-error pattern rather than a revision
 * conflict dialog.
 *
 * Callers must remount this component (via a changing `key`) each time it is opened for a
 * different collection/mode so its internal form state initializes fresh - see
 * CollectionsNav/ModelsPage for the `key` usage. This avoids resetting state from an effect.
 */
export function CollectionFormModal({ isOpen, collection, isSaving = false, onSubmit, onClose }: CollectionFormModalProps) {
  const nameId = useId();
  const descriptionId = useId();
  const [name, setName] = useState(collection?.name ?? '');
  const [description, setDescription] = useState(collection?.description ?? '');
  const [nameError, setNameError] = useState<string | null>(null);

  const isEditing = !!collection;

  const handleSubmit = (event: FormEvent) => {
    event.preventDefault();
    const trimmedName = name.trim();
    if (!trimmedName) {
      setNameError('Collection name is required.');
      return;
    }
    if (trimmedName.length > MAX_NAME_LENGTH) {
      setNameError(`Collection name must be ${MAX_NAME_LENGTH} characters or fewer.`);
      return;
    }
    setNameError(null);
    onSubmit({ name: trimmedName, description: description.trim() || undefined });
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={isEditing ? 'Rename Collection' : 'New Collection'}
      isDisabled={isSaving}
      footer={
        <div className="flex justify-end gap-3 w-full">
          <Button type="button" variant="secondary" onClick={onClose} disabled={isSaving}>
            Cancel
          </Button>
          <Button type="submit" form="collection-form" variant="primary" loading={isSaving} disabled={isSaving}>
            {isEditing ? 'Save' : 'Create'}
          </Button>
        </div>
      }
    >
      <form id="collection-form" onSubmit={handleSubmit} className="space-y-4" noValidate>
        <FormField label="Name" htmlFor={nameId} required error={nameError ?? undefined}>
          <Input
            id={nameId}
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="e.g. Miniatures, Client work, Print-in-progress"
            maxLength={MAX_NAME_LENGTH}
            invalid={!!nameError}
            disabled={isSaving}
            autoFocus
            required
          />
        </FormField>
        <FormField label="Description" htmlFor={descriptionId} helper="Optional. Visible to anyone who can see this collection.">
          <Textarea
            id={descriptionId}
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="What is this collection for?"
            maxLength={MAX_DESCRIPTION_LENGTH}
            disabled={isSaving}
            rows={3}
          />
        </FormField>
      </form>
    </Modal>
  );
}
