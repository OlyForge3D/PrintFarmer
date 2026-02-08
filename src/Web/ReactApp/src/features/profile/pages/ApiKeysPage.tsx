import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, Toggle } from '@/common/components/ui';
import { KeyIcon, PlusIcon, DeleteIcon, RefreshIcon, EyeIcon, EyeOffIcon } from '@/common/components/icons/MdiIcons';
import {
  listApiKeys,
  createApiKey,
  toggleApiKey,
  deleteApiKey,
  rotateApiKey,
  revealApiKey,
  getApiKeySettings,
  type ApiKeyDto,
} from '@/services/apiKeysService';

export function ApiKeysPage() {
  const { user } = useAuth();
  const queryClient = useQueryClient();
  const [newKeyName, setNewKeyName] = useState('');
  const [showCreateForm, setShowCreateForm] = useState(false);
  const [createdKey, setCreatedKey] = useState<{ key: string; id: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [revealedKeys, setRevealedKeys] = useState<Record<string, string>>({});

  const userId = user?.id;

  // Fetch API key settings (whether hashing is enabled)
  const { data: settings } = useQuery({
    queryKey: ['apiKeySettings'],
    queryFn: getApiKeySettings,
  });

  const canRevealKeys = settings?.hashingEnabled === false;

  // Fetch API keys
  const { data: apiKeys = [], isLoading, error: fetchError } = useQuery({
    queryKey: ['apiKeys', userId],
    queryFn: () => {
      if (!userId) throw new Error('User ID required');
      return listApiKeys(userId);
    },
    enabled: !!userId,
  });

  // Create API key mutation
  const createMutation = useMutation({
    mutationFn: (name: string) => {
      if (!userId) throw new Error('User ID required');
      return createApiKey(userId, { name });
    },
    onSuccess: (data) => {
      setCreatedKey(data);
      setNewKeyName('');
      setShowCreateForm(false);
      setError(null);
      queryClient.invalidateQueries({ queryKey: ['apiKeys', userId] });
    },
    onError: (err) => {
      setError(err instanceof Error ? err.message : 'Failed to create API key');
    },
  });

  // Toggle API key mutation
  const toggleMutation = useMutation({
    mutationFn: ({ keyId }: { keyId: string }) => {
      if (!userId) throw new Error('User ID required');
      return toggleApiKey(userId, keyId);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['apiKeys', userId] });
    },
    onError: (err) => {
      setError(err instanceof Error ? err.message : 'Failed to toggle API key');
    },
  });

  // Delete API key mutation
  const deleteMutation = useMutation({
    mutationFn: ({ keyId }: { keyId: string }) => {
      if (!userId) throw new Error('User ID required');
      return deleteApiKey(userId, keyId);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['apiKeys', userId] });
    },
    onError: (err) => {
      setError(err instanceof Error ? err.message : 'Failed to delete API key');
    },
  });

  // Rotate API key mutation
  const rotateMutation = useMutation({
    mutationFn: ({ keyId }: { keyId: string }) => {
      if (!userId) throw new Error('User ID required');
      return rotateApiKey(userId, keyId);
    },
    onSuccess: (data) => {
      setCreatedKey(data);
      queryClient.invalidateQueries({ queryKey: ['apiKeys', userId] });
    },
    onError: (err) => {
      setError(err instanceof Error ? err.message : 'Failed to rotate API key');
    },
  });

  const handleCreate = () => {
    if (!newKeyName.trim()) {
      setError('API key name is required');
      return;
    }
    createMutation.mutate(newKeyName.trim());
  };

  const handleToggle = (keyId: string) => {
    toggleMutation.mutate({ keyId });
  };

  const handleDelete = (keyId: string, keyName: string) => {
    if (confirm(`Are you sure you want to delete API key "${keyName}"? This cannot be undone.`)) {
      deleteMutation.mutate({ keyId });
    }
  };

  const handleRotate = (keyId: string, keyName: string) => {
    if (confirm(`Are you sure you want to rotate API key "${keyName}"? The old key will stop working immediately.`)) {
      rotateMutation.mutate({ keyId });
    }
  };

  const handleReveal = async (keyId: string) => {
    if (!userId) return;
    
    // If already revealed, hide it
    if (revealedKeys[keyId]) {
      setRevealedKeys(prev => {
        const next = { ...prev };
        delete next[keyId];
        return next;
      });
      return;
    }

    // Fetch and reveal the key
    try {
      const response = await revealApiKey(userId, keyId);
      setRevealedKeys(prev => ({ ...prev, [keyId]: response.key }));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to reveal API key');
    }
  };

  const copyToClipboard = (text: string) => {
    navigator.clipboard.writeText(text);
    alert('API key copied to clipboard!');
  };

  const formatDate = (dateString: string) => {
    return new Date(dateString).toLocaleString();
  };

  return (
    <PageTemplate
      title="API Keys"
      subtitle="Manage API keys for OctoPrint-compatible slicer integration"
      icon={KeyIcon}
    >
      <div className="space-y-6">
        {/* Information Banner */}
        <div className="bg-pf-info/10 border border-pf-info rounded-lg p-4">
          <h3 className="font-semibold text-pf-text-primary mb-2">What are API Keys?</h3>
          <p className="text-pf-text-secondary text-sm">
            API keys allow slicers (PrusaSlicer, OrcaSlicer, etc.) to upload G-code files directly to PrintFarmer.
            Configure your slicer with your PrintFarmer server URL and an API key to enable seamless integration.
          </p>
          <p className="text-pf-text-secondary text-sm mt-2">
            <strong>Security:</strong> Treat API keys like passwords. Don't share them or commit them to version control.
          </p>
        </div>

        {/* Created Key Display */}
        {createdKey && (
          <div className="bg-pf-success/10 border border-pf-success rounded-lg p-4">
            <h3 className="font-semibold text-pf-success mb-2">API Key Created Successfully</h3>
            <p className="text-pf-text-secondary text-sm mb-3">
              <strong className="text-pf-warning">Important:</strong> Copy this API key now. You won't be able to see it again!
            </p>
            <div className="bg-pf-bg-2 p-3 rounded-sm border border-pf-border font-mono text-sm break-all">
              {createdKey.key}
            </div>
            <div className="mt-3 flex gap-2">
              <Button
                variant="primary"
                onClick={() => copyToClipboard(createdKey.key)}
              >
                Copy to Clipboard
              </Button>
              <Button
                variant="secondary"
                onClick={() => setCreatedKey(null)}
              >
                Done
              </Button>
            </div>
          </div>
        )}

        {/* Error Display */}
        {(error || fetchError) && (
          <div className="bg-pf-error/10 border border-pf-error rounded-lg p-4 text-pf-error">
            {error || (fetchError instanceof Error ? fetchError.message : 'Failed to load API keys')}
          </div>
        )}

        {/* Create New API Key */}
        <div className="bg-pf-bg-1 rounded-lg p-6 border border-pf-border">
          <div className="flex justify-between items-center mb-4">
            <h2 className="text-lg font-semibold text-pf-text-primary">Your API Keys</h2>
            {!showCreateForm && (
              <Button
                variant="primary"
                onClick={() => setShowCreateForm(true)}
                iconLeft={<PlusIcon className="w-4 h-4" />}
              >
                Create New API Key
              </Button>
            )}
          </div>

          {showCreateForm && (
            <div className="mb-6 p-4 bg-pf-bg-2 rounded-sm border border-pf-border">
              <h3 className="font-semibold text-pf-text-primary mb-3">Create New API Key</h3>
              <div className="flex gap-2">
                <input
                  type="text"
                  value={newKeyName}
                  onChange={(e) => setNewKeyName(e.target.value)}
                  placeholder="Enter a descriptive name (e.g., 'PrusaSlicer Workstation')"
                  className="flex-1 px-3 py-2 bg-pf-bg-1 border border-pf-border rounded-sm text-pf-text-primary focus:outline-hidden focus:ring-2 focus:ring-pf-primary"
                  onKeyDown={(e) => e.key === 'Enter' && handleCreate()}
                />
                <Button
                  variant="primary"
                  onClick={handleCreate}
                  disabled={createMutation.isPending}
                >
                  {createMutation.isPending ? 'Creating...' : 'Create'}
                </Button>
                <Button
                  variant="secondary"
                  onClick={() => {
                    setShowCreateForm(false);
                    setNewKeyName('');
                    setError(null);
                  }}
                >
                  Cancel
                </Button>
              </div>
            </div>
          )}

          {/* API Keys List */}
          {isLoading ? (
            <div className="text-center text-pf-text-secondary py-8">Loading API keys...</div>
          ) : apiKeys.length === 0 ? (
            <div className="text-center text-pf-text-secondary py-8">
              <KeyIcon className="w-12 h-12 mx-auto mb-2 opacity-50" />
              <p>No API keys yet. Create one to get started!</p>
            </div>
          ) : (
            <div className="space-y-3">
              {apiKeys.map((apiKey: ApiKeyDto) => (
                <div
                  key={apiKey.id}
                  className="p-4 bg-pf-bg-2 rounded-sm border border-pf-border"
                >
                  <div className="flex items-center justify-between">
                    <div className="flex-1">
                      <div className="flex items-center gap-3">
                        <h3 className="font-semibold text-pf-text-primary">{apiKey.name}</h3>
                        <span
                          className={`px-2 py-0.5 rounded text-xs font-medium ${
                            apiKey.isActive
                              ? 'bg-pf-success/20 text-pf-success'
                              : 'bg-pf-text-secondary/20 text-pf-text-secondary'
                          }`}
                        >
                          {apiKey.isActive ? 'Active' : 'Disabled'}
                        </span>
                      </div>
                      <p className="text-sm text-pf-text-secondary mt-1">
                        Created: {formatDate(apiKey.createdAt)}
                        {apiKey.expiresAt && ` • Expires: ${formatDate(apiKey.expiresAt)}`}
                      </p>
                    </div>
                    <div className="flex gap-2 items-center">
                      <Toggle
                        checked={apiKey.isActive}
                        onChange={() => handleToggle(apiKey.id)}
                        disabled={toggleMutation.isPending}
                        size="sm"
                        aria-label={apiKey.isActive ? 'Disable API key' : 'Enable API key'}
                      />
                      {canRevealKeys && (
                        <Button
                          variant="secondary"
                          onClick={() => handleReveal(apiKey.id)}
                          iconLeft={revealedKeys[apiKey.id] ? <EyeOffIcon className="w-4 h-4" /> : <EyeIcon className="w-4 h-4" />}
                          title={revealedKeys[apiKey.id] ? 'Hide API key' : 'Reveal API key'}
                        />
                      )}
                      <Button
                        variant="secondary"
                        onClick={() => handleRotate(apiKey.id, apiKey.name)}
                        disabled={rotateMutation.isPending}
                        iconLeft={<RefreshIcon className="w-4 h-4" />}
                        title="Rotate (generate new key)"
                      />
                      <Button
                        variant="danger"
                        onClick={() => handleDelete(apiKey.id, apiKey.name)}
                        disabled={deleteMutation.isPending}
                        iconLeft={<DeleteIcon className="w-4 h-4" />}
                        title="Delete"
                      />
                    </div>
                  </div>
                  {/* Revealed Key Display */}
                  {revealedKeys[apiKey.id] && (
                    <div className="mt-3 pt-3 border-t border-pf-border">
                      <div className="flex items-center gap-2">
                        <code className="flex-1 bg-pf-bg-1 px-3 py-2 rounded-sm border border-pf-border font-mono text-sm break-all text-pf-text-primary">
                          {revealedKeys[apiKey.id]}
                        </code>
                        <Button
                          variant="secondary"
                          onClick={() => copyToClipboard(revealedKeys[apiKey.id])}
                          title="Copy to clipboard"
                        >
                          Copy
                        </Button>
                      </div>
                    </div>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>

        {/* Documentation Link */}
        <div className="bg-pf-bg-1 rounded-lg p-6 border border-pf-border">
          <h2 className="text-lg font-semibold text-pf-text-primary mb-2">
            Need Help?
          </h2>
          <p className="text-pf-text-secondary text-sm">
            See the <a href="/docs/SLICER_CONFIGURATION.md" className="text-pf-primary hover:underline">Slicer Configuration Guide</a> for
            step-by-step instructions on configuring PrusaSlicer, OrcaSlicer, and other slicers to work with PrintFarmer.
          </p>
        </div>
      </div>
    </PageTemplate>
  );
}
