import { useState, useId } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Select, Input, Textarea, FormField, NumberStepper } from '@/common/components/ui';
import { Checkbox } from '@/common/components/ui/Checkbox';
import { useCreateSpool } from '@/common/hooks/useApi';
import type { SpoolmanUpdateSpoolRequest } from '@/types/api';
import type { SpoolmanSpoolDto } from '@/features/filamentManagement/types';
import { toast } from 'sonner';
import { apiClient } from '@/services/api';
import { useQuery } from '@tanstack/react-query';

interface AddSpoolModalProps {
  isOpen: boolean;
  onClose: () => void;
  /** If provided, pre-populates form fields (clone mode). */
  sourceSpool?: SpoolmanSpoolDto | null;
  onSuccess: () => void;
}

/**
 * Modal for creating a new Spoolman spool. When sourceSpool is provided,
 * the form is pre-populated for cloning.
 *
 * Built with accessibility in mind — manual testing recommended.
 */
export function AddSpoolModal({ isOpen, onClose, sourceSpool, onSuccess }: AddSpoolModalProps) {
  return (
    <AddSpoolFormModal
      key={sourceSpool?.id ?? 'new'}
      isOpen={isOpen}
      onClose={onClose}
      sourceSpool={sourceSpool}
      onSuccess={onSuccess}
    />
  );
}

function AddSpoolFormModal({ isOpen, onClose, sourceSpool, onSuccess }: AddSpoolModalProps) {
  const formId = useId();
  const htmlFormId = `add-spool-form-${sourceSpool?.id ?? 'new'}`;
  const createMutation = useCreateSpool();

  const isClone = sourceSpool != null;
  const title = isClone ? `Clone Spool` : 'Add Spool';

  const { data: filaments = [], isLoading: filamentsLoading } = useQuery({
    queryKey: ['filament-types'],
    queryFn: () => apiClient.getFilaments(),
    enabled: isOpen,
  });

  // For clone, resolve the filament ID from the source spool's filamentName
  const resolvedFilamentId = (() => {
    if (!sourceSpool?.filamentName || filaments.length === 0) return '';
    const match = filaments.find(f => f.name === sourceSpool.filamentName);
    return match ? String(match.id) : '';
  })();

  const [filamentId, setFilamentId] = useState('');
  const [filamentTouched, setFilamentTouched] = useState(false);
  const [remainingWeight, setRemainingWeight] = useState(sourceSpool?.remainingWeightG != null ? String(sourceSpool.remainingWeightG) : '');
  const [initialWeight, setInitialWeight] = useState(sourceSpool?.initialWeightG != null ? String(sourceSpool.initialWeightG) : '');
  const [spoolWeight, setSpoolWeight] = useState(sourceSpool?.spoolWeightG != null ? String(sourceSpool.spoolWeightG) : '');
  const [location, setLocation] = useState(sourceSpool?.location ?? '');
  const [lotNumber, setLotNumber] = useState(sourceSpool?.lotNumber ?? '');
  const [price, setPrice] = useState(sourceSpool?.price != null ? String(sourceSpool.price) : '');
  const [comment, setComment] = useState(sourceSpool?.comment ?? '');
  const [archived, setArchived] = useState(false);
  const [quantity, setQuantity] = useState(1);

  const effectiveFilamentId = filamentTouched ? filamentId : resolvedFilamentId;

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();

    if (!effectiveFilamentId) {
      toast.error('Filament is required.');
      return;
    }

    const request: SpoolmanUpdateSpoolRequest = {
      filamentId: Number(effectiveFilamentId),
    };

    // Numeric: send if user entered a value (allows 0)
    if (remainingWeight !== '') request.remainingWeight = Number(remainingWeight);
    if (initialWeight !== '') request.initialWeight = Number(initialWeight);
    if (spoolWeight !== '') request.spoolWeight = Number(spoolWeight);
    if (price !== '') request.price = Number(price);
    // String: always send to allow clearing
    request.location = location.trim();
    request.lotNumber = lotNumber.trim();
    request.comment = comment.trim();
    if (archived) request.archived = true;

    try {
      const count = Math.max(1, Math.min(quantity, 100));
      const results = await Promise.allSettled(
        Array.from({ length: count }, () => createMutation.mutateAsync(request))
      );
      const created = results.filter(r => r.status === 'fulfilled').length;
      const failed = count - created;
      if (failed > 0) {
        toast.warning(`${created}/${count} spools created, ${failed} failed.`);
      } else {
        toast.success(count === 1 ? 'Spool created.' : `${count} spools created.`);
      }
      onSuccess();
      onClose();
    } catch {
      toast.error('Failed to create spool(s).');
    }
  };

  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={title}
      width="max-w-2xl"
      closeOnEscape
      footer={
        <div className="flex items-center gap-2">
          <NumberStepper
            id={`${formId}-quantity`}
            value={quantity}
            onChange={setQuantity}
            min={1}
            max={100}
            aria-label="Number of spools to create"
            className="mr-auto"
          />
          <Button variant="secondary" size="sm" className="h-8" onClick={onClose}>
            Cancel
          </Button>
          <Button
            variant="primary"
            size="sm"
            className="h-8"
            type="submit"
            form={htmlFormId}
            disabled={createMutation.isPending}
          >
            {createMutation.isPending ? 'Creating...' : isClone ? 'Clone' : 'Add'}
          </Button>
        </div>
      }
    >
      <form id={htmlFormId} onSubmit={handleSubmit} className="space-y-4 text-sm">
        <FormField label="Filament" htmlFor={`${formId}-filament`} required>
          <Select
            id={`${formId}-filament`}
            aria-label="Filament"
            aria-required="true"
            value={effectiveFilamentId}
            onChange={e => { setFilamentTouched(true); setFilamentId(e.target.value); }}
            disabled={filamentsLoading}
          >
            <option value="">— Select filament —</option>
            {[...filaments].sort((a, b) => {
              const aLabel = `${a.vendor || ''} ${a.name}`.trim();
              const bLabel = `${b.vendor || ''} ${b.name}`.trim();
              return aLabel.localeCompare(bLabel);
            }).map(f => (
              <option key={f.id} value={f.id}>
                {f.vendor ? `${f.vendor} — ` : ''}{f.name}{f.material ? ` (${f.material})` : ''}
              </option>
            ))}
          </Select>
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
    </Modal>
  );
}
