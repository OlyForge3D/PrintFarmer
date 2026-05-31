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
  NfcTagMismatchEvent,
  NfcLinkRequest,
  NfcLinkResponse,
  NfcPairingStep,
} from '@/features/nfc/types';

interface NfcPairingModalProps {
  event: NfcTagUnknownEvent | null;
  isOpen: boolean;
  onClose: () => void;
}

interface NfcMismatchModalProps {
  event: NfcTagMismatchEvent | null;
  isOpen: boolean;
  onClose: () => void;
  onRelink: () => void;
}

// Stub for POST /api/nfc/link (backend #362 in flight)
async function linkNfcTag(request: NfcLinkRequest): Promise<NfcLinkResponse> {
  const response = await apiClient.client.post('/nfc/link', request);
  return response.data as NfcLinkResponse;
}

export function NfcPairingModal({ event, isOpen, onClose }: NfcPairingModalProps) {
  const [step, setStep] = useState<NfcPairingStep>('detected');
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedSpool, setSelectedSpool] = useState<SpoolmanSpool | null>(null);
  const [errorMessage, setErrorMessage] = useState('');

  // Track the event to detect when a new one arrives and reset via key
  const [trackedEvent, setTrackedEvent] = useState<NfcTagUnknownEvent | null>(null);
  if (isOpen && event && event !== trackedEvent) {
    setTrackedEvent(event);
    setStep('detected');
    setSearchQuery('');
    setSelectedSpool(null);
    setErrorMessage('');
  }

  // Auto-advance from detected to search after brief delay
  useEffect(() => {
    if (step === 'detected') {
      const timer = setTimeout(() => setStep('search'), 1200);
      return () => clearTimeout(timer);
    }
  }, [step]);

  // Auto-close on success
  useEffect(() => {
    if (step === 'success') {
      const timer = setTimeout(() => onClose(), 2000);
      return () => clearTimeout(timer);
    }
  }, [step, onClose]);

  // Fetch spools for search
  const { data: spoolsData, isLoading: spoolsLoading } = useQuery({
    queryKey: ['spoolman-spools-nfc-search', searchQuery],
    queryFn: () => apiClient.getSpools({ search: searchQuery || undefined, limit: 50 }),
    enabled: isOpen && step === 'search',
  });

  const spools = useMemo(() => spoolsData?.items ?? [], [spoolsData]);

  // Link mutation
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
      deviceId: event.deviceId,
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

  if (!event) return null;

  const title = step === 'success'
    ? 'Tag Linked'
    : step === 'error'
      ? 'Link Failed'
      : 'Pair NFC Tag';

  return (
    <Modal isOpen={isOpen} onClose={onClose} title={title} size="lg">
      <div className="space-y-4">
        {/* Detected state */}
        {step === 'detected' && (
          <div className="flex flex-col items-center gap-3 py-6">
            <Spinner size="lg" />
            <p className="text-pf-text-secondary text-sm">Tag detected</p>
            <Badge variant="default">{event.tagUid}</Badge>
            {event.deviceName && (
              <p className="text-pf-text-secondary text-xs">
                Reader: {event.deviceName}
              </p>
            )}
          </div>
        )}

        {/* Search state */}
        {step === 'search' && (
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

        {/* Confirm state */}
        {step === 'confirm' && selectedSpool && (
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

        {/* Success state */}
        {step === 'success' && (
          <div className="flex flex-col items-center gap-3 py-6">
            <div className="w-12 h-12 rounded-full bg-green-500/20 flex items-center justify-center">
              <svg className="w-6 h-6 text-green-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
              </svg>
            </div>
            <p className="text-pf-text-primary font-medium">Tag linked successfully</p>
            <p className="text-pf-text-secondary text-sm">Closing automatically...</p>
          </div>
        )}

        {/* Error state */}
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
      </div>
    </Modal>
  );
}

export function NfcMismatchModal({ event, isOpen, onClose, onRelink }: NfcMismatchModalProps) {
  if (!event) return null;

  return (
    <Modal isOpen={isOpen} onClose={onClose} title="Tag Mismatch" size="md">
      <div className="space-y-4">
        <p className="text-pf-text-secondary text-sm">
          This tag is already linked to a different spool. Would you like to relink it?
        </p>

        <div className="bg-pf-bg-2 border border-pf-border rounded-lg p-4 space-y-2">
          <div className="flex justify-between text-sm">
            <span className="text-pf-text-secondary">Tag</span>
            <Badge variant="default">{event.tagUid}</Badge>
          </div>
          <div className="flex justify-between text-sm">
            <span className="text-pf-text-secondary">Currently linked to</span>
            <span className="text-pf-text-primary">{event.currentSpoolName ?? `Spool #${event.currentSpoolId}`}</span>
          </div>
          {event.expectedSpoolName && (
            <div className="flex justify-between text-sm">
              <span className="text-pf-text-secondary">Expected</span>
              <span className="text-pf-text-primary">{event.expectedSpoolName}</span>
            </div>
          )}
        </div>

        <div className="flex gap-3 justify-end pt-2">
          <Button variant="subtle" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="danger" onClick={onRelink}>
            Relink
          </Button>
        </div>
      </div>
    </Modal>
  );
}
