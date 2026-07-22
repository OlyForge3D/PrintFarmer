import { useState, useEffect, useMemo, useCallback } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui/Button';
import { Input } from '@/common/components/ui/Input';
import { Spinner } from '@/common/components/ui/Spinner';
import { Badge } from '@/common/components/ui/Badge';
import { apiClient } from '@/services/api';
import type { SpoolmanSpool } from '@/types/api';
import type {
  NfcTagUnknownEvent,
  NfcLinkRequest,
  NfcLinkResponse,
  NfcPairingStep,
} from '@/features/nfc/types';
import type { NfcPairingSession } from '@/features/nfc/hooks/useNfcPairingSession';

interface NfcPairingModalProps {
  session: NfcPairingSession;
}

async function linkNfcTag(request: NfcLinkRequest): Promise<NfcLinkResponse> {
  const response = await apiClient.client.post('/nfc/link', request);
  return response.data as NfcLinkResponse;
}

export function NfcPairingModal({ session }: NfcPairingModalProps) {
  const { isOpen, tagEvent: event, isUnavailable, close: onClose } = session;

  const [step, setStep] = useState<NfcPairingStep>(() => {
    if (isUnavailable) return 'unavailable';
    if (event) return 'detected';
    return 'scanning';
  });
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedSpool, setSelectedSpool] = useState<SpoolmanSpool | null>(null);
  const [errorMessage, setErrorMessage] = useState('');

  // Track the event identity to reset state when a new tag arrives.
  const [trackedEvent, setTrackedEvent] = useState<NfcTagUnknownEvent | null>(null);

  // Transition to unavailable when hub drops mid-session (derived state, not effect).
  if (isUnavailable && isOpen && step !== 'unavailable') {
    setStep('unavailable');
  }

  // When a new tag event arrives, reset the flow.
  if (isOpen && event && event !== trackedEvent) {
    setTrackedEvent(event);
    setStep('detected');
    setSearchQuery('');
    setSelectedSpool(null);
    setErrorMessage('');
  }

  // When modal opens without a tag (startScanning), show scanning step.
  if (isOpen && !event && !isUnavailable && step !== 'scanning' && step !== 'unavailable') {
    setStep('scanning');
  }

  // Auto-advance from detected to search.
  useEffect(() => {
    if (step === 'detected') {
      const timer = setTimeout(() => setStep('search'), 1200);
      return () => clearTimeout(timer);
    }
  }, [step]);

  // Auto-close on success.
  useEffect(() => {
    if (step === 'success') {
      const timer = setTimeout(() => onClose(), 2000);
      return () => clearTimeout(timer);
    }
  }, [step, onClose]);

  const { data: spoolsData, isLoading: spoolsLoading } = useQuery({
    queryKey: ['spoolman-spools-nfc-search', searchQuery],
    queryFn: () => apiClient.getSpools({ search: searchQuery || undefined, limit: 50 }),
    enabled: isOpen && step === 'search',
  });

  const spools = useMemo(() => spoolsData?.items ?? [], [spoolsData]);

  const linkMutation = useMutation<NfcLinkResponse, Error, NfcLinkRequest>({
    mutationFn: linkNfcTag,
    onSuccess: () => {
      setStep('success');
      toast.success('Tag linked to spool');
    },
    onError: (err) => {
      setErrorMessage(err.message || 'Failed to link tag');
      setStep('error');
    },
  });

  const handleConfirm = useCallback(() => {
    if (!event || !selectedSpool) return;
    linkMutation.mutate({
      tagUid: event.tagUid,
      spoolId: selectedSpool.id,
      printerId: event.printerId,
    });
  }, [event, selectedSpool, linkMutation]);

  const handleSelectSpool = useCallback((spool: SpoolmanSpool) => {
    setSelectedSpool(spool);
    setStep('confirm');
  }, []);

  const handleRetry = useCallback(() => {
    setErrorMessage('');
    setStep('search');
  }, []);

  const handleClose = useCallback(() => {
    setStep('scanning');
    setTrackedEvent(null);
    setSelectedSpool(null);
    setSearchQuery('');
    setErrorMessage('');
    onClose();
  }, [onClose]);

  if (!isOpen) return null;

  const title =
    step === 'success' ? 'Tag Linked' :
    step === 'error' ? 'Link Failed' :
    step === 'unavailable' ? 'NFC Unavailable' :
    'Pair NFC Tag';

  return (
    <Modal isOpen={isOpen} onClose={handleClose} title={title} size="lg">
      <div className="space-y-4">
        {/* Scanning — waiting for tag */}
        {step === 'scanning' && (
          <div className="flex flex-col items-center gap-3 py-6">
            <Spinner size="lg" />
            <p className="text-pf-text-secondary text-sm">Waiting for NFC tag…</p>
            <p className="text-pf-text-secondary text-xs">Hold a tag near the reader to begin pairing.</p>
          </div>
        )}

        {/* Detected — tag arrived, brief acknowledgement */}
        {step === 'detected' && event && (
          <div className="flex flex-col items-center gap-3 py-6">
            <Spinner size="lg" />
            <p className="text-pf-text-secondary text-sm">Tag detected</p>
            <Badge variant="default">{event.tagUid}</Badge>
          </div>
        )}

        {/* Search — spool selection */}
        {step === 'search' && event && (
          <div className="space-y-3">
            <div className="flex items-center gap-2">
              <Badge variant="default">{event.tagUid}</Badge>
              <span className="text-pf-text-secondary text-xs">→ Select a spool to link</span>
            </div>

            <Input
              placeholder="Search by name, vendor, or material..."
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              autoFocus
            />

            <div className="max-h-64 overflow-y-auto border border-pf-border rounded-lg">
              {spoolsLoading ? (
                <div className="flex justify-center py-6">
                  <Spinner size="md" />
                </div>
              ) : spools.length === 0 ? (
                <p className="text-pf-text-secondary text-sm text-center py-6">
                  {searchQuery ? 'No spools match your search' : 'No spools available'}
                </p>
              ) : (
                <ul className="divide-y divide-pf-border" role="listbox" aria-label="Spool search results">
                  {spools.map((spool) => (
                    <li key={spool.id}>
                      <Button
                        variant="ghost"
                        className="w-full px-4 py-3 text-left hover:bg-pf-bg-2 transition-colors flex items-center gap-3 rounded-none h-auto"
                        onClick={() => handleSelectSpool(spool)}
                        role="option"
                        aria-selected={false}
                      >
                        {spool.colorHex && (
                          <span
                            className="w-4 h-4 rounded-full shrink-0 border border-pf-border"
                            style={{ backgroundColor: `#${spool.colorHex.replace('#', '')}` }}
                          />
                        )}
                        <div className="flex-1 min-w-0">
                          <p className="text-pf-text-primary text-sm font-medium truncate">
                            {spool.name || spool.filamentName || `Spool #${spool.id}`}
                          </p>
                          <p className="text-pf-text-secondary text-xs truncate">
                            {[spool.vendor, spool.material].filter(Boolean).join(' · ')}
                          </p>
                        </div>
                        {spool.remainingPercent != null && (
                          <span className="text-pf-text-secondary text-xs shrink-0">
                            {Math.round(spool.remainingPercent)}%
                          </span>
                        )}
                      </Button>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          </div>
        )}

        {/* Confirm */}
        {step === 'confirm' && selectedSpool && event && (
          <div className="space-y-4">
            <p className="text-pf-text-secondary text-sm">Link this tag to the selected spool?</p>

            <div className="bg-pf-bg-2 border border-pf-border rounded-lg p-4 flex items-center gap-3">
              {selectedSpool.colorHex && (
                <span
                  className="w-6 h-6 rounded-full shrink-0 border border-pf-border"
                  style={{ backgroundColor: `#${selectedSpool.colorHex.replace('#', '')}` }}
                />
              )}
              <div className="flex-1">
                <p className="text-pf-text-primary font-medium">
                  {selectedSpool.name || selectedSpool.filamentName || `Spool #${selectedSpool.id}`}
                </p>
                <p className="text-pf-text-secondary text-sm">
                  {[selectedSpool.vendor, selectedSpool.material].filter(Boolean).join(' · ')}
                </p>
              </div>
            </div>

            <div className="flex items-center gap-2 text-xs text-pf-text-secondary">
              <span>Tag:</span>
              <Badge variant="default">{event.tagUid}</Badge>
            </div>

            <div className="flex gap-3 justify-end pt-2">
              <Button variant="subtle" onClick={() => setStep('search')}>
                Back
              </Button>
              <Button
                variant="primary"
                onClick={handleConfirm}
                disabled={linkMutation.isPending}
              >
                {linkMutation.isPending ? <Spinner size="sm" /> : 'Link'}
              </Button>
            </div>
          </div>
        )}

        {/* Success */}
        {step === 'success' && (
          <div className="flex flex-col items-center gap-3 py-6">
            <div className="w-12 h-12 rounded-full bg-green-500/20 flex items-center justify-center">
              <svg className="w-6 h-6 text-green-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
              </svg>
            </div>
            <p className="text-pf-text-primary font-medium">Tag linked successfully</p>
            <p className="text-pf-text-secondary text-sm">Closing automatically…</p>
          </div>
        )}

        {/* Error */}
        {step === 'error' && (
          <div className="flex flex-col items-center gap-3 py-6">
            <div className="w-12 h-12 rounded-full bg-red-500/20 flex items-center justify-center">
              <svg className="w-6 h-6 text-red-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
              </svg>
            </div>
            <p className="text-pf-text-primary font-medium">Failed to link tag</p>
            <p className="text-pf-text-secondary text-sm">{errorMessage}</p>
            <Button variant="subtle" onClick={handleRetry}>
              Try Again
            </Button>
          </div>
        )}

        {/* Unavailable — hub dropped */}
        {step === 'unavailable' && (
          <div className="flex flex-col items-center gap-3 py-6">
            <div className="w-12 h-12 rounded-full bg-amber-500/20 flex items-center justify-center">
              <svg className="w-6 h-6 text-amber-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                  d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
              </svg>
            </div>
            <p className="text-pf-text-primary font-medium">NFC reader unavailable</p>
            <p className="text-pf-text-secondary text-sm">
              The connection to the NFC hub was lost. Check your server connection and try again.
            </p>
            <Button variant="subtle" onClick={handleClose}>
              Close
            </Button>
          </div>
        )}
      </div>
    </Modal>
  );
}

