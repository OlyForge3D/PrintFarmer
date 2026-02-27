import { useState, useId } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Input, Textarea, FormField } from '@/common/components/ui';
import { Checkbox } from '@/common/components/ui/Checkbox';
import { useUpdateSpool } from '@/common/hooks/useApi';
import type { SpoolmanUpdateSpoolRequest } from '@/types/api';
import type { SpoolmanSpoolDto } from '@/features/filamentManagement/types';
import { toast } from 'sonner';

interface EditSpoolModalProps {
  isOpen: boolean;
  onClose: () => void;
  spool: SpoolmanSpoolDto | null;
  onSuccess: () => void;
}

/**
 * Modal for editing a single Spoolman spool's properties.
 * Pre-populates fields from the spool being edited.
 *
 * Built with accessibility in mind — manual testing recommended.
 */
export function EditSpoolModal({ isOpen, onClose, spool, onSuccess }: EditSpoolModalProps) {
  return (
    <EditSpoolFormModal
      key={spool?.id ?? 'none'}
      isOpen={isOpen}
      onClose={onClose}
      spool={spool}
      onSuccess={onSuccess}
    />
  );
}

function EditSpoolFormModal({ isOpen, onClose, spool, onSuccess }: EditSpoolModalProps) {
  const formId = useId();
  const htmlFormId = `edit-spool-form-${spool?.id ?? 'none'}`;
  const updateMutation = useUpdateSpool();

  const [remainingWeight, setRemainingWeight] = useState(spool?.remainingWeightG != null ? String(spool.remainingWeightG) : '');
  const [initialWeight, setInitialWeight] = useState(spool?.initialWeightG != null ? String(spool.initialWeightG) : '');
  const [spoolWeight, setSpoolWeight] = useState(spool?.spoolWeightG != null ? String(spool.spoolWeightG) : '');
  const [location, setLocation] = useState(spool?.location ?? '');
  const [lotNumber, setLotNumber] = useState(spool?.lotNumber ?? '');
  const [price, setPrice] = useState(spool?.price != null ? String(spool.price) : '');
  const [comment, setComment] = useState(spool?.comment ?? '');
  const [archived, setArchived] = useState(spool?.archived ?? false);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!spool) return;

    const request: SpoolmanUpdateSpoolRequest = {};

    // Numeric: send if user entered a value (allows 0)
    if (remainingWeight !== '') request.remainingWeight = Number(remainingWeight);
    if (initialWeight !== '') request.initialWeight = Number(initialWeight);
    if (spoolWeight !== '') request.spoolWeight = Number(spoolWeight);
    if (price !== '') request.price = Number(price);
    // String: always send to allow clearing
    request.location = location.trim();
    request.lotNumber = lotNumber.trim();
    request.comment = comment.trim();
    request.archived = archived;

    try {
      await updateMutation.mutateAsync({ id: spool.id, request });
      toast.success(`Spool #${spool.id} updated.`);
      onSuccess();
      onClose();
    } catch {
      toast.error('Failed to update spool.');
    }
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={`Edit Spool #${spool?.id ?? ''}: ${spool?.filamentName || spool?.name || 'Unknown'}`}
      width="max-w-2xl"
      closeOnEscape
      footer={
        <div className="flex gap-2">
          <Button variant="secondary" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            size="sm"
            type="submit"
            form={htmlFormId}
            disabled={updateMutation.isPending}
          >
            {updateMutation.isPending ? 'Saving...' : 'Save Changes'}
          </Button>
        </div>
      }
    >
      {spool && (
        <form id={htmlFormId} onSubmit={handleSubmit} className="space-y-4 text-sm">
          {/* Filament info (read-only) */}
          <FormField label="Filament" htmlFor={`${formId}-filament`}>
            <div
              id={`${formId}-filament`}
              className="px-3 py-2 text-sm bg-pf-bg-2 border border-pf-border rounded-md text-pf-text-secondary"
            >
              {spool.filamentName || spool.name || 'Unknown'} ({spool.vendor || 'No vendor'})
            </div>
          </FormField>

          <div className="grid grid-cols-3 gap-4">
            <FormField label="Remaining (g)" htmlFor={`${formId}-remaining`}>
              <Input
                id={`${formId}-remaining`}
                type="number"
                step="0.1"
                min="0"
                value={remainingWeight}
                onChange={e => setRemainingWeight(e.target.value)}
                placeholder="0"
                aria-label="Remaining weight in grams"
              />
            </FormField>
            <FormField label="Initial Weight (g)" htmlFor={`${formId}-initial`}>
              <Input
                id={`${formId}-initial`}
                type="number"
                step="0.1"
                min="0"
                value={initialWeight}
                onChange={e => setInitialWeight(e.target.value)}
                placeholder="1000"
                aria-label="Initial weight in grams"
              />
            </FormField>
            <FormField label="Spool Weight (g)" htmlFor={`${formId}-spool-wt`}>
              <Input
                id={`${formId}-spool-wt`}
                type="number"
                step="0.1"
                min="0"
                value={spoolWeight}
                onChange={e => setSpoolWeight(e.target.value)}
                placeholder="Empty spool weight"
                aria-label="Empty spool weight in grams"
              />
            </FormField>
          </div>

          <div className="grid grid-cols-3 gap-4">
            <FormField label="Location" htmlFor={`${formId}-location`}>
              <Input
                id={`${formId}-location`}
                type="text"
                value={location}
                onChange={e => setLocation(e.target.value)}
                placeholder="Shelf A"
                aria-label="Storage location"
              />
            </FormField>
            <FormField label="Lot Number" htmlFor={`${formId}-lot`}>
              <Input
                id={`${formId}-lot`}
                type="text"
                value={lotNumber}
                onChange={e => setLotNumber(e.target.value)}
                placeholder="LOT-123"
                aria-label="Lot number"
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
                placeholder="24.99"
                aria-label="Price"
              />
            </FormField>
          </div>

          <FormField label="Comment" htmlFor={`${formId}-comment`}>
            <Textarea
              id={`${formId}-comment`}
              value={comment}
              onChange={e => setComment(e.target.value)}
              placeholder="Optional notes..."
              rows={2}
              className="resize-none"
              aria-label="Comment"
            />
          </FormField>

          <label className="flex items-center gap-2 text-sm cursor-pointer">
            <Checkbox
              checked={archived}
              onChange={e => setArchived(e.target.checked)}
              aria-label="Mark spool as archived"
            />
            Archived
          </label>
        </form>
      )}
    </Modal>
  );
}
