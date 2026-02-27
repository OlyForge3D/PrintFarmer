import { useState, useId } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Input, Textarea, FormField, Select } from '@/common/components/ui';
import { useBulkUpdateSpools } from '@/common/hooks/useApi';
import type { SpoolmanBulkUpdateSpoolsRequest, SpoolmanBulkUpdateResult } from '@/types/api';
import { toast } from 'sonner';

interface BulkEditSpoolsModalProps {
  isOpen: boolean;
  onClose: () => void;
  selectedIds: number[];
  onSuccess: () => void;
}

/**
 * Modal for bulk-editing selected Spoolman spools.
 * Only fields that are explicitly set (non-empty) are sent in the update.
 *
 * Built with accessibility in mind — manual testing recommended.
 */
export function BulkEditSpoolsModal({ isOpen, onClose, selectedIds, onSuccess }: BulkEditSpoolsModalProps) {
  const formId = useId();
  const bulkUpdate = useBulkUpdateSpools();

  const [location, setLocation] = useState('');
  const [lotNumber, setLotNumber] = useState('');
  const [price, setPrice] = useState('');
  const [comment, setComment] = useState('');
  const [archivedAction, setArchivedAction] = useState<'' | 'true' | 'false'>('');

  const resetForm = () => {
    setLocation('');
    setLotNumber('');
    setPrice('');
    setComment('');
    setArchivedAction('');
  };

  const handleSubmit = async () => {
    const request: SpoolmanBulkUpdateSpoolsRequest = {
      spoolIds: selectedIds,
    };

    if (location.trim()) request.location = location.trim();
    if (lotNumber.trim()) request.lotNumber = lotNumber.trim();
    if (price !== '') request.price = Number(price);
    if (comment.trim()) request.comment = comment.trim();
    if (archivedAction === 'true') request.archived = true;
    if (archivedAction === 'false') request.archived = false;

    const hasChanges = location.trim() || lotNumber.trim() || price !== '' || comment.trim() || archivedAction;
    if (!hasChanges) {
      toast.error('No fields set. Select at least one field to update.');
      return;
    }

    try {
      const result: SpoolmanBulkUpdateResult = await bulkUpdate.mutateAsync(request);
      if (result.errorCount > 0) {
        toast.warning(`Updated ${result.updatedCount}, ${result.errorCount} errors: ${result.errors.slice(0, 3).join('; ')}`);
      } else {
        toast.success(`Successfully updated ${result.updatedCount} spool${result.updatedCount !== 1 ? 's' : ''}.`);
      }
      resetForm();
      onSuccess();
      onClose();
    } catch {
      toast.error('Bulk update failed.');
    }
  };

  const handleClose = () => {
    resetForm();
    onClose();
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title={`Bulk Edit ${selectedIds.length} Spool${selectedIds.length !== 1 ? 's' : ''}`}
      size="md"
      closeOnEscape
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="secondary" size="sm" onClick={handleClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            size="sm"
            onClick={handleSubmit}
            disabled={bulkUpdate.isPending}
          >
            {bulkUpdate.isPending ? 'Updating...' : 'Apply Changes'}
          </Button>
        </div>
      }
    >
      <div className="space-y-4 text-sm">
        <p className="text-pf-text-secondary">
          Only fields you fill in below will be applied. Leave a field blank to keep existing values.
        </p>

        <FormField label="Location" htmlFor={`${formId}-location`}>
          <Input
            id={`${formId}-location`}
            type="text"
            value={location}
            onChange={e => setLocation(e.target.value)}
            placeholder="e.g. Shelf A"
            aria-label="Bulk update location"
          />
        </FormField>

        <FormField label="Lot Number" htmlFor={`${formId}-lot`}>
          <Input
            id={`${formId}-lot`}
            type="text"
            value={lotNumber}
            onChange={e => setLotNumber(e.target.value)}
            placeholder="e.g. LOT-123"
            aria-label="Bulk update lot number"
          />
        </FormField>

        <FormField label="Price" htmlFor={`${formId}-price`}>
          <Input
            id={`${formId}-price`}
            type="number"
            step="0.01"
            min="0"
            value={price}
            onChange={e => setPrice(e.target.value)}
            placeholder="e.g. 24.99"
            aria-label="Bulk update price"
          />
        </FormField>

        <FormField label="Archived" htmlFor={`${formId}-archived`}>
          <Select
            id={`${formId}-archived`}
            value={archivedAction}
            onChange={e => setArchivedAction(e.target.value as '' | 'true' | 'false')}
            aria-label="Bulk update archived status"
          >
            <option value="">— No change —</option>
            <option value="true">Archive all</option>
            <option value="false">Unarchive all</option>
          </Select>
        </FormField>

        <FormField label="Comment" htmlFor={`${formId}-comment`}>
          <Textarea
            id={`${formId}-comment`}
            value={comment}
            onChange={e => setComment(e.target.value)}
            placeholder="Optional note..."
            rows={2}
            className="resize-none"
            aria-label="Bulk update comment"
          />
        </FormField>
      </div>
    </Modal>
  );
}
