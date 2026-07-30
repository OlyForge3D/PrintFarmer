import { useState, type FormEvent } from 'react';
import { toast } from 'sonner';
import { Alert, Button, Input } from '@/common/components/ui';
import { Modal } from '@/common/components/modals/Modal';
import { useRegisterBinBarcode } from '../hooks/usePartsInventory';
import type { RegisterBinBarcodeRequest } from '@/types/partsInventory';
import { getErrorMessage } from '../utils/problemDetails';

export interface RegisterBinModalProps {
  isOpen: boolean;
  onClose: () => void;
}

/**
 * RegisterBinModal — attach a scanned code to a bin. If the code already
 * matches a bin the server returns it (200); otherwise a new bin is
 * created with the supplied name/location (201). This is intentionally
 * scanner-friendly: the `code` field autofocuses so a barcode reader
 * types straight into it.
 */
export function RegisterBinModal({ isOpen, onClose }: RegisterBinModalProps) {
  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Register bin barcode" size="md">
      {/* key forces fresh state on each open */}
      <RegisterBinBody key={String(isOpen)} onClose={onClose} />
    </Modal>
  );
}

function RegisterBinBody({ onClose }: { onClose: () => void }) {
  const register = useRegisterBinBarcode();
  const [code, setCode] = useState('');
  const [name, setName] = useState('');
  const [location, setLocation] = useState('');

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault();
    const trimmedCode = code.trim();
    if (!trimmedCode) {
      toast.error('Bin code / barcode is required');
      return;
    }
    const request: RegisterBinBarcodeRequest = {
      code: trimmedCode,
      name: name.trim() || null,
      location: location.trim() || null,
    };
    try {
      const outcome = await register.mutateAsync(request);
      if (outcome.wasCreated) {
        toast.success(`Registered new bin ${outcome.bin.code}`);
      } else {
        toast.success(`Bin ${outcome.bin.code} already registered`);
      }
      onClose();
    } catch (error) {
      toast.error(getErrorMessage(error, 'Failed to register bin'));
    }
  };

  const isSaving = register.isPending;

  return (
    <form onSubmit={handleSubmit} className="space-y-4" noValidate>
      <Alert type="info">
        Scan a barcode into the code field. Existing bins are looked up in-place;
        unknown codes create a new bin with the name and location below.
      </Alert>
      <div>
        <label htmlFor="reg-bin-code" className="block text-sm font-medium text-pf-text-secondary mb-1">
          Bin code / barcode <span className="text-pf-error">*</span>
        </label>
        <Input
          id="reg-bin-code"
          value={code}
          onChange={(event) => setCode(event.target.value)}
          placeholder="Scan or type"
          required
          maxLength={128}
          autoFocus
        />
      </div>
      <div>
        <label htmlFor="reg-bin-name" className="block text-sm font-medium text-pf-text-secondary mb-1">
          Name
        </label>
        <Input
          id="reg-bin-name"
          value={name}
          onChange={(event) => setName(event.target.value)}
          placeholder="Only applied when creating a new bin"
          maxLength={200}
          aria-describedby="reg-bin-name-help"
        />
        <p id="reg-bin-name-help" className="text-xs text-pf-text-secondary mt-1">
          Defaults to the code when omitted.
        </p>
      </div>
      <div>
        <label htmlFor="reg-bin-location" className="block text-sm font-medium text-pf-text-secondary mb-1">
          Location
        </label>
        <Input
          id="reg-bin-location"
          value={location}
          onChange={(event) => setLocation(event.target.value)}
          placeholder="Optional — used only for new bins"
          maxLength={200}
        />
      </div>
      <div className="flex justify-end gap-2 pt-2">
        <Button type="button" variant="secondary" size="sm" onClick={onClose} disabled={isSaving}>
          Cancel
        </Button>
        <Button type="submit" variant="primary" size="sm" loading={isSaving}>
          Register
        </Button>
      </div>
    </form>
  );
}

