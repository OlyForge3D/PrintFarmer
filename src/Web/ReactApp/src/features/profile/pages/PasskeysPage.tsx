import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button } from '@/common/components/ui';
import { KeyIcon, PlusIcon, DeleteIcon, EditIcon } from '@/common/components/icons/MdiIcons';
import { Modal } from '@/common/components/modals/Modal';
import {
  listPasskeys,
  deletePasskey,
  renamePasskey,
  type PasskeyCredentialDto,
} from '@/services/passkeyService';

export function PasskeysPage() {
  const queryClient = useQueryClient();
  const [deleteTarget, setDeleteTarget] = useState<PasskeyCredentialDto | null>(null);
  const [editTarget, setEditTarget] = useState<PasskeyCredentialDto | null>(null);
  const [editName, setEditName] = useState('');

  const { data: passkeys = [], isLoading } = useQuery({
    queryKey: ['passkeys'],
    queryFn: listPasskeys,
  });

  const deleteMutation = useMutation({
    mutationFn: (id: number) => deletePasskey(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['passkeys'] });
      toast.success('Passkey removed');
      setDeleteTarget(null);
    },
    onError: () => {
      toast.error('Failed to remove passkey');
    },
  });

  const renameMutation = useMutation({
    mutationFn: ({ id, name }: { id: number; name: string }) => renamePasskey(id, name),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['passkeys'] });
      toast.success('Passkey renamed');
      setEditTarget(null);
    },
    onError: () => {
      toast.error('Failed to rename passkey');
    },
  });

  function handleStartEdit(passkey: PasskeyCredentialDto) {
    setEditTarget(passkey);
    setEditName(passkey.deviceName || passkey.aaguidDescription || '');
  }

  function handleSaveRename() {
    if (!editTarget || !editName.trim()) return;
    renameMutation.mutate({ id: editTarget.id, name: editName.trim() });
  }

  function formatDate(dateStr: string | null): string {
    if (!dateStr) return 'Never';
    return new Date(dateStr).toLocaleDateString(undefined, {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
    });
  }

  function getDisplayName(passkey: PasskeyCredentialDto): string {
    return passkey.deviceName || passkey.aaguidDescription || 'Unnamed passkey';
  }

  return (
    <PageTemplate
      title="Passkeys"
      icon={<KeyIcon className="w-6 h-6" />}
      description="Manage your registered passkeys for passwordless sign-in."
    >
      <div className="space-y-4">
        <div className="flex justify-end">
          <Button
            variant="primary"
            onClick={() => {
              // Navigate to register flow — triggers enrollment ceremony
              window.location.href = '/profile/passkeys/register';
            }}
          >
            <PlusIcon className="w-4 h-4" />
            <span>Add passkey</span>
          </Button>
        </div>

        {isLoading && (
          <div className="text-center py-8 text-pf-text-secondary">Loading passkeys…</div>
        )}

        {!isLoading && passkeys.length === 0 && (
          <div className="text-center py-8 text-pf-text-secondary">
            <KeyIcon className="w-12 h-12 mx-auto mb-2 opacity-40" />
            <p>No passkeys registered yet.</p>
            <p className="text-sm mt-1">Add a passkey for fast, secure sign-in.</p>
          </div>
        )}

        {!isLoading && passkeys.length > 0 && (
          <div className="border border-pf-border rounded-lg divide-y divide-pf-border">
            {passkeys.map((passkey) => (
              <div
                key={passkey.id}
                className="flex items-center justify-between px-4 py-3 gap-4"
              >
                <div className="flex-1 min-w-0">
                  <div className="font-medium text-pf-text-primary truncate">
                    {getDisplayName(passkey)}
                  </div>
                  <div className="text-sm text-pf-text-secondary">
                    Created {formatDate(passkey.createdAt)}
                    {passkey.lastUsedAt && <> · Last used {formatDate(passkey.lastUsedAt)}</>}
                  </div>
                </div>
                <div className="flex items-center gap-2 flex-shrink-0">
                  <Button
                    variant="subtle"
                    size="sm"
                    onClick={() => handleStartEdit(passkey)}
                    aria-label={`Rename ${getDisplayName(passkey)}`}
                  >
                    <EditIcon className="w-4 h-4" />
                  </Button>
                  <Button
                    variant="subtle"
                    size="sm"
                    onClick={() => setDeleteTarget(passkey)}
                    aria-label={`Remove ${getDisplayName(passkey)}`}
                  >
                    <DeleteIcon className="w-4 h-4" />
                  </Button>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Delete confirmation modal */}
      <Modal
        isOpen={!!deleteTarget}
        onClose={() => setDeleteTarget(null)}
        title="Remove passkey"
        size="sm"
        footer={
          <div className="flex justify-end gap-2">
            <Button variant="subtle" onClick={() => setDeleteTarget(null)}>
              Cancel
            </Button>
            <Button
              variant="danger"
              onClick={() => deleteTarget && deleteMutation.mutate(deleteTarget.id)}
              disabled={deleteMutation.isPending}
            >
              {deleteMutation.isPending ? 'Removing…' : 'Remove'}
            </Button>
          </div>
        }
      >
        <p>
          Are you sure you want to remove{' '}
          <strong>{deleteTarget ? getDisplayName(deleteTarget) : ''}</strong>?
          You won't be able to sign in with this passkey anymore.
        </p>
      </Modal>

      {/* Rename modal */}
      <Modal
        isOpen={!!editTarget}
        onClose={() => setEditTarget(null)}
        title="Rename passkey"
        size="sm"
        footer={
          <div className="flex justify-end gap-2">
            <Button variant="subtle" onClick={() => setEditTarget(null)}>
              Cancel
            </Button>
            <Button
              variant="primary"
              onClick={handleSaveRename}
              disabled={renameMutation.isPending || !editName.trim()}
            >
              {renameMutation.isPending ? 'Saving…' : 'Save'}
            </Button>
          </div>
        }
      >
        <label className="block text-sm font-medium text-pf-text-primary mb-1">
          Device name
        </label>
        <input
          type="text"
          className="w-full px-3 py-2 border border-pf-border rounded-md bg-pf-bg-primary text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent"
          value={editName}
          onChange={(e) => setEditName(e.target.value)}
          maxLength={100}
          autoFocus
          onKeyDown={(e) => {
            if (e.key === 'Enter') handleSaveRename();
          }}
        />
      </Modal>
    </PageTemplate>
  );
}
