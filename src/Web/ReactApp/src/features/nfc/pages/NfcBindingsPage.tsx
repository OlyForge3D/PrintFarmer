import { useState } from 'react';
import { Button, Card, Badge, Spinner } from '@/common/components/ui';
import { PageTemplate } from '@/common/components/PageTemplate';
import { useNfcBindings, useDeleteNfcBinding } from '@/common/hooks/useApi';
import type { NfcBindingDto } from '@/features/nfc/types';

function formatTimeAgo(dateStr?: string): string {
  if (!dateStr) return 'Never';
  const diff = Date.now() - new Date(dateStr).getTime();
  const mins = Math.floor(diff / 60000);
  if (mins < 1) return 'Just now';
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.floor(hours / 24)}d ago`;
}

export function NfcBindingsPage() {
  const { data: bindings = [], isLoading, error } = useNfcBindings();
  const deleteMutation = useDeleteNfcBinding();
  const [confirmId, setConfirmId] = useState<string | null>(null);

  if (isLoading) {
    return <PageTemplate title="NFC Tag Bindings"><Spinner size="lg" /></PageTemplate>;
  }

  if (error) {
    return (
      <PageTemplate title="NFC Tag Bindings">
        <div className="p-4 text-pf-error">Failed to load NFC bindings: {String(error)}</div>
      </PageTemplate>
    );
  }

  const handleUnbind = (id: string) => {
    deleteMutation.mutate(id, { onSuccess: () => setConfirmId(null) });
  };

  return (
    <PageTemplate
      title="NFC Tag Bindings"
      subtitle={`${bindings.length} bound tag${bindings.length !== 1 ? 's' : ''}`}
    >
      {bindings.length === 0 ? (
        <Card>
          <div className="p-8 text-center text-pf-text-secondary">
            No NFC tags have been bound yet. Scan a tag to get started.
          </div>
        </Card>
      ) : (
        <div className="grid gap-4">
          {bindings.map((binding: NfcBindingDto) => (
            <Card key={binding.id}>
              <div className="flex items-center justify-between p-4">
                <div className="space-y-1">
                  <div className="flex items-center gap-2">
                    <span className="font-mono text-sm text-pf-text-primary">{binding.tagUid}</span>
                    {binding.spoolName && (
                      <Badge variant="primary">{binding.spoolName}</Badge>
                    )}
                  </div>
                  <div className="text-xs text-pf-text-secondary">
                    Printer: {binding.printerName ?? binding.printerId}
                    {binding.trayId && ` • Tray: ${binding.trayId}`}
                  </div>
                  <div className="text-xs text-pf-text-tertiary">
                    Last seen: {formatTimeAgo(binding.spoolLastSeenAt)}
                  </div>
                </div>
                <div>
                  {confirmId === binding.id ? (
                    <div className="flex items-center gap-2">
                      <Button
                        variant="danger"
                        size="sm"
                        onClick={() => handleUnbind(binding.id)}
                        disabled={deleteMutation.isPending}
                      >
                        Confirm
                      </Button>
                      <Button variant="subtle" size="sm" onClick={() => setConfirmId(null)}>
                        Cancel
                      </Button>
                    </div>
                  ) : (
                    <Button variant="subtle" size="sm" onClick={() => setConfirmId(binding.id)}>
                      Unbind
                    </Button>
                  )}
                </div>
              </div>
            </Card>
          ))}
        </div>
      )}
    </PageTemplate>
  );
}
