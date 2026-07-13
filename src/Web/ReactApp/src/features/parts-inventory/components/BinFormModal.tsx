import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { Button, Checkbox, Input } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { useCreateBin, useUpdateBin } from '../hooks/usePartsInventory';
import type { BinDto, CreateBinRequest, UpdateBinRequest } from '@/types/partsInventory';
import { getErrorMessage } from '../utils/problemDetails';

export interface BinFormModalProps {
  isOpen: boolean;
  onClose: () => void;
  bin?: BinDto | null;
}

/**
 * BinFormModal — create or edit a storage bin. The bin's `code` doubles
 * as its barcode; the {@link RegisterBinModal} exposes a scan-friendly
 * flow that auto-creates a bin from a code when it does not exist.
 */
export function BinFormModal({ isOpen, onClose, bin }: BinFormModalProps) {
  const isEdit = Boolean(bin);
  return (
    <Modal
      isOpen={isOpen}
      onClose={onClose}
      title={isEdit ? `Edit bin: ${bin?.code}` : 'Add bin'}
      size="md"
    >
      {/* key forces fresh state when the target bin changes or the modal reopens */}
      <BinFormBody key={`${bin?.id ?? 'new'}-${isOpen}`} bin={bin ?? null} onClose={onClose} />
    </Modal>
  );
}

interface BinFormBodyProps {
  bin: BinDto | null;
  onClose: () => void;
}

function BinFormBody({ bin, onClose }: BinFormBodyProps) {
  const isEdit = Boolean(bin);
  const createBin = useCreateBin();
  const updateBin = useUpdateBin();

  const [code, setCode] = useState(bin?.code ?? '');
  const [name, setName] = useState(bin?.name ?? '');
  const [location, setLocation] = useState(bin?.location ?? '');
  const [notes, setNotes] = useState(bin?.notes ?? '');
  const [isActive, setIsActive] = useState(bin?.isActive ?? true);

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault();
    const trimmedCode = code.trim();
    const trimmedName = name.trim();
    if (!trimmedCode) {
      toast.error('Bin code is required');
      return;
    }
    if (!trimmedName) {
      toast.error('Bin name is required');
      return;
    }
    try {
      if (isEdit && bin) {
        const request: UpdateBinRequest = {
          name: trimmedName,
          location: location.trim() || null,
          notes: notes.trim() || null,
          isActive,
        };
        await updateBin.mutateAsync({ code: bin.code, request });
        toast.success('Bin updated');
      } else {
        const request: CreateBinRequest = {
          code: trimmedCode,
          name: trimmedName,
          location: location.trim() || null,
          notes: notes.trim() || null,
        };
        await createBin.mutateAsync(request);
        toast.success('Bin created');
      }
      onClose();
    } catch (error) {
      toast.error(getErrorMessage(error, 'Failed to save bin'));
    }
  };

  const isSaving = createBin.isPending || updateBin.isPending;

  return (
    <form onSubmit={handleSubmit} className="space-y-4" noValidate>
      <div>
        <label htmlFor="bin-code" className="block text-sm font-medium text-pf-text-secondary mb-1">
          Code / barcode <span className="text-pf-error">*</span>
        </label>
        <Input
          id="bin-code"
          value={code}
          onChange={(event) => setCode(event.target.value)}
          placeholder="A-01"
          required
          maxLength={128}
          disabled={isEdit}
          aria-describedby="bin-code-help"
        />
        <p id="bin-code-help" className="text-xs text-pf-text-secondary mt-1">
          Doubles as the scannable barcode. Codes are normalized by the server.
        </p>
      </div>
      <div>
        <label htmlFor="bin-name" className="block text-sm font-medium text-pf-text-secondary mb-1">
          Name <span className="text-pf-error">*</span>
        </label>
        <Input
          id="bin-name"
          value={name}
          onChange={(event) => setName(event.target.value)}
          placeholder="Shelf A, bin 1"
          required
          maxLength={200}
        />
      </div>
      <div>
        <label htmlFor="bin-location" className="block text-sm font-medium text-pf-text-secondary mb-1">
          Location
        </label>
        <Input
          id="bin-location"
          value={location}
          onChange={(event) => setLocation(event.target.value)}
          placeholder="Shelf, room, cart, etc."
          maxLength={200}
        />
      </div>
      <div>
        <label htmlFor="bin-notes" className="block text-sm font-medium text-pf-text-secondary mb-1">
          Notes
        </label>
        <Input
          id="bin-notes"
          value={notes}
          onChange={(event) => setNotes(event.target.value)}
          placeholder="Optional"
          maxLength={1000}
        />
      </div>
      {isEdit && (
        <Checkbox
          checked={isActive}
          onChange={(event) => setIsActive(event.target.checked)}
          label="Active"
        />
      )}
      <div className="flex justify-end gap-2 pt-2">
        <Button type="button" variant="secondary" size="sm" onClick={onClose} disabled={isSaving}>
          Cancel
        </Button>
        <Button type="submit" variant="primary" size="sm" loading={isSaving}>
          {isEdit ? 'Save changes' : 'Create bin'}
        </Button>
      </div>
    </form>
  );
}

