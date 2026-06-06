import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, FormField, Input } from '@/common/components/ui';
import { KeyIcon, PlusIcon, DeleteIcon, EditIcon } from '@/common/components/icons/MdiIcons';
import { Modal } from '@/common/components/modals/Modal';
import {
  listPasskeys,
  deletePasskey,
  renamePasskey,
  registerPasskey,
  type PasskeyCredentialDto,
} from '@/services/passkeyService';

interface PasskeysPageProps {
  embedded?: boolean;
}

export function PasskeysPage({ embedded = false }: PasskeysPageProps) {
  const queryClient = useQueryClient();
  const [deleteTarget, setDeleteTarget] = useState<PasskeyCredentialDto | null>(null);
  const [editTarget, setEditTarget] = useState<PasskeyCredentialDto | null>(null);
  const [editName, setEditName] = useState('');
  const [showRegisterModal, setShowRegisterModal] = useState(false);
  const [pendingDeviceName, setPendingDeviceName] = useState('');

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

  const registerMutation = useMutation({
    mutationFn: async (deviceName?: string) => {
      const result = await registerPasskey();
      if (deviceName?.trim()) {
        await renamePasskey(result.newCredentialId, deviceName.trim());
      }
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['passkeys'] });
      toast.success('Passkey registered successfully');
      setShowRegisterModal(false);
      setPendingDeviceName('');
    },
    onError: (error: Error) => {
      toast.error(error.message || 'Failed to register passkey');
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

  const content = (
    <>
      <div className="space-y-4">
        <div className="flex justify-end">
          <Button
            variant="primary"
            onClick={() => setShowRegisterModal(true)}
            disabled={registerMutation.isPending}
          >
            <PlusIcon className="w-4 h-4" />
            <span>Add passkey</span>
          </Button>
        </div>

        {isLoading && (
          <div className="py-8 text-center text-pf-text-secondary">Loading passkeys…</div>
        )}

        {!isLoading && passkeys.length === 0 && (
          <div className="py-8 text-center text-pf-text-secondary">
            <KeyIcon className="mx-auto mb-2 h-12 w-12 opacity-40" />
            <p>No passkeys registered yet.</p>
            <p className="mt-1 text-sm">Add a passkey for fast, secure sign-in.</p>
          </div>
        )}

        {!isLoading && passkeys.length > 0 && (
          <div className="divide-y divide-pf-border rounded-lg border border-pf-border">
            {passkeys.map((passkey) => (
              <div
                key={passkey.id}
                className="flex items-center justify-between gap-4 px-4 py-3"
              >
                <div className="min-w-0 flex-1">
                  <div className="truncate font-medium text-pf-text-primary">
                    {getDisplayName(passkey)}
                  </div>
                  <div className="text-sm text-pf-text-secondary">
                    Created {formatDate(passkey.createdAt)}
                    {passkey.lastUsedAt && <> · Last used {formatDate(passkey.lastUsedAt)}</>}
                  </div>
                </div>
                <div className="flex shrink-0 items-center gap-2">
                  <Button
                    variant="subtle"
                    size="sm"
                    onClick={() => handleStartEdit(passkey)}
                    aria-label={`Rename ${getDisplayName(passkey)}`}
                  >
                    <EditIcon className="h-4 w-4" />
                  </Button>
                  <Button
                    variant="subtle"
                    size="sm"
                    onClick={() => setDeleteTarget(passkey)}
                    aria-label={`Remove ${getDisplayName(passkey)}`}
                  >
                    <DeleteIcon className="h-4 w-4" />
                  </Button>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Register passkey modal */}
      <Modal
        isOpen={showRegisterModal}
        onClose={() => {
          if (!registerMutation.isPending) {
            setShowRegisterModal(false);
            setPendingDeviceName('');
          }
        }}
        title="Add passkey"
        size="sm"
        footer={
          <div className="flex justify-end gap-2">
            <Button
              variant="subtle"
              onClick={() => {
                setShowRegisterModal(false);
                setPendingDeviceName('');
              }}
              disabled={registerMutation.isPending}
            >
              Cancel
            </Button>
            <Button
              variant="primary"
              onClick={() => registerMutation.mutate(pendingDeviceName || undefined)}
              disabled={registerMutation.isPending}
              loading={registerMutation.isPending}
            >
              {registerMutation.isPending ? 'Registering…' : 'Register passkey'}
            </Button>
          </div>
        }
      >
        <div className="space-y-3">
          <p className="text-sm text-pf-text-secondary">
            Your browser will prompt you to create a passkey. Optionally give it a name to
            identify it later — or leave blank to use the device description.
          </p>
          <FormField label="Device name (optional)" htmlFor="passkey-device-name">
            <Input
              id="passkey-device-name"
              type="text"
              value={pendingDeviceName}
              onChange={(e) => setPendingDeviceName(e.target.value)}
              placeholder="e.g. MacBook Pro, iPhone 15"
              maxLength={100}
              disabled={registerMutation.isPending}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && !registerMutation.isPending) {
                  registerMutation.mutate(pendingDeviceName || undefined);
                }
              }}
            />
          </FormField>
        </div>
      </Modal>

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
        <label
          htmlFor="rename-passkey-input"
          className="block text-sm font-medium text-pf-text-primary mb-1"
        >
          Device name
        </label>
        <Input
          id="rename-passkey-input"
          type="text"
          value={editName}
          onChange={(e) => setEditName(e.target.value)}
          maxLength={100}
          autoFocus
          onKeyDown={(e) => {
            if (e.key === 'Enter') handleSaveRename();
          }}
        />
      </Modal>
    </>
  );

  if (embedded) {
    return content;
  }

  return (
    <PageTemplate
      title="Passkeys"
      icon={KeyIcon}
      subtitle="Manage your registered passkeys for passwordless sign-in."
    >
      {content}
    </PageTemplate>
  );
}
