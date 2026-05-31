import { useState } from 'react';
import { Modal } from '@/common/components/modals/Modal';
import { Button, Spinner } from '@/common/components/ui';
import { Select } from '@/common/components/ui/Select';
import { FormField } from '@/common/components/ui/FormField';
import { usePrinters, useLinkNfcTag } from '@/common/hooks/useApi';
import type { NfcTagUnknownEvent } from '@/features/nfc/types';
import { parseSpoolId } from '@/features/nfc/types/nfc';

interface NfcBindingModalProps {
  isOpen: boolean;
  onClose: () => void;
  event: NfcTagUnknownEvent | null;
}

export function NfcBindingModal({ isOpen, onClose, event }: NfcBindingModalProps) {
  const { data: printers = [], isLoading: printersLoading } = usePrinters();
  const linkMutation = useLinkNfcTag();

  const [selectedPrinterId, setSelectedPrinterId] = useState<string>(event?.printerId ?? '');
  const [spoolId, setSpoolId] = useState('');

  const handleSubmit = () => {
    if (!event?.tagUid || !selectedPrinterId) return;

    linkMutation.mutate(
      {
        tagUid: event.tagUid,
        printerId: selectedPrinterId,
        spoolId: parseSpoolId(spoolId),
      },
      { onSuccess: () => onClose() }
    );
  };

  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Bind NFC Tag" size="md">
      <div className="space-y-4">
        <FormField label="Tag UID">
          <input
            type="text"
            readOnly
            value={event?.tagUid ?? ''}
            aria-label="Tag UID"
            className="w-full rounded border border-pf-border bg-pf-bg-secondary px-3 py-2 text-sm text-pf-text-primary"
          />
        </FormField>

        <FormField label="Printer">
          {printersLoading ? (
            <Spinner size="sm" />
          ) : (
            <Select
              value={selectedPrinterId}
              onChange={(e) => setSelectedPrinterId(e.target.value)}
              label="Printer"
            >
              <option value="">Select printer...</option>
              {printers.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </Select>
          )}
        </FormField>

        <FormField label="Spool ID (optional)">
          <input
            type="text"
            value={spoolId}
            onChange={(e) => setSpoolId(e.target.value)}
            placeholder="Enter spool ID to bind"
            aria-label="Spool ID"
            className="w-full rounded border border-pf-border bg-pf-bg-secondary px-3 py-2 text-sm text-pf-text-primary"
          />
        </FormField>
      </div>

      <div className="mt-6 flex justify-end gap-3">
        <Button variant="subtle" onClick={onClose}>
          Cancel
        </Button>
        <Button
          variant="primary"
          onClick={handleSubmit}
          disabled={!selectedPrinterId || linkMutation.isPending}
        >
          {linkMutation.isPending ? 'Binding...' : 'Bind Tag'}
        </Button>
      </div>
    </Modal>
  );
}
