import { useEffect, useRef, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, Toggle, FormField, Select, Checkbox, Input, Badge } from '@/common/components/ui';
import { KeyIcon, PlusIcon, DeleteIcon, RefreshIcon, EyeIcon, EyeOffIcon } from '@/common/components/icons/MdiIcons';
import {
  listApiKeys,
  createApiKey,
  toggleApiKey,
  deleteApiKey,
  rotateApiKey,
  revealApiKey,
  getApiKeySettings,
  resolveScopeNames,
  type ApiKeyDto,
  type ApiKeyPurpose,
  type ApiKeyScope,
  type CreateApiKeyRequest,
  type CreateApiKeyResponse,
} from '@/services/apiKeysService';

interface ScopeOption {
  value: ApiKeyScope;
  label: string;
  description: string;
  /** Rendered as an explicit warning when this scope carries real-world impact. */
  impact?: string;
}

interface ScopeGroup {
  id: string;
  title: string;
  description: string;
  options: ScopeOption[];
}

/**
 * Scopes are presented as independent checkboxes, grouped only for readability. There is
 * deliberately no "all calibration" or "select all" toggle: every scope maps to exactly one
 * server-side permission, and a bulk toggle makes it too easy to grant destructive or
 * physically-actuating authority without reading what it does.
 */
const SCOPE_GROUPS: ScopeGroup[] = [
  {
    id: 'library',
    title: 'Model library',
    description: 'Access to the 3D model library. Grants no calibration, slicing, or printing authority.',
    options: [
      { value: 'ModelRead', label: 'Model read', description: 'Read 3D model/library metadata and files.' },
      { value: 'ModelWrite', label: 'Model write', description: 'Create, update, or delete model/library entries.' },
      { value: 'LibrarySync', label: 'Library sync', description: 'Sync the local desktop model library with the server.' },
    ],
  },
  {
    id: 'calibration',
    title: 'Calibration',
    description: 'Each option grants exactly one calibration permission to this key.',
    options: [
      { value: 'CalibrationRead', label: 'Calibration read', description: 'View calibration projects, attempts, photos, and generated profiles.' },
      { value: 'CalibrationCreate', label: 'Calibration create', description: 'Create calibration projects and attempts.' },
      { value: 'CalibrationUpdate', label: 'Calibration update', description: 'Edit calibration projects, drafts, observations, and photos.' },
      {
        value: 'CalibrationDelete',
        label: 'Calibration delete',
        description: 'Delete calibration projects, drafts, and photos.',
        impact: 'Destructive — permanently removes calibration data.',
      },
      {
        value: 'CalibrationGenerate',
        label: 'Calibration generate',
        description: 'Produce and export generated calibration profiles.',
        impact: 'Also requires Calibration read, Slicing submit, and Slicing read artifact.',
      },
      { value: 'CalibrationPublish', label: 'Calibration publish', description: 'Publish a generated calibration profile revision for others to use.' },
    ],
  },
  {
    id: 'slicing',
    title: 'Slicing',
    description: 'Submitting slice jobs is separate from generating calibration profiles.',
    options: [
      {
        value: 'SlicingSubmit',
        label: 'Slicing submit',
        description: 'Submit slicing jobs, and read the slicer profile catalog.',
        impact: 'Consumes slicer worker capacity. Cannot modify profiles.',
      },
      { value: 'SlicingReadArtifact', label: 'Slicing read artifact', description: 'Download sliced G-code artifacts.' },
    ],
  },
  {
    id: 'queue',
    title: 'Print queue',
    description: 'Required only if this key needs to queue or run physical prints.',
    options: [
      { value: 'QueueRead', label: 'Queue read', description: 'View the print queue.' },
      { value: 'QueueWrite', label: 'Queue write', description: 'Add and edit print jobs. Also requires Queue read.' },
      {
        value: 'QueueStart',
        label: 'Queue start',
        description: 'Start a job on a printer. Also requires Queue read.',
        impact: 'Starts a physical print on real hardware.',
      },
      {
        value: 'QueueCancel',
        label: 'Queue cancel',
        description: 'Cancel a job. Also requires Queue read.',
        impact: 'Stops a physical print already in progress.',
      },
      {
        value: 'QueueAcknowledgeBedClear',
        label: 'Queue acknowledge bed clear',
        description: 'Confirm the bed is clear so the next job may start. Also requires Queue read and Queue start.',
        impact: 'Lets the farm start the next physical print.',
      },
    ],
  },
];

const MAX_KEY_LIFETIME_MS = 365 * 24 * 60 * 60 * 1000;

// Static, generic message shown when a create/rotate response is malformed (missing/empty
// secret or the display metadata needed to render it safely). Deliberately contains no
// details from the response itself so it can never leak partial secret data.
const MALFORMED_SECRET_RESPONSE_ERROR = 'The server response was missing required API key data. Please try again.';

/** The one-time secret plus the minimum metadata needed to render/dismiss it. */
interface RevealedSecret {
  key: string;
  id: string;
}

interface CreateKeyFieldErrors {
  name?: string;
  scopes?: string;
  expiry?: string;
}

/** Stable identity of whatever triggered the current one-time secret, used to restore focus. */
type FocusRestoreTarget = { type: 'create' } | { type: 'rotate'; keyId: string };

/**
 * Validates a create/rotate response and extracts only the one-time secret plus the id
 * needed to display it. Throws a generic, static error (never echoing response content)
 * when the secret or required metadata is missing/empty so callers can fail safely without
 * ever assigning the raw response — or any fragment of it — to component/query state.
 */
function extractOneTimeSecret(response: CreateApiKeyResponse): RevealedSecret {
  if (!response || typeof response.key !== 'string' || response.key.trim() === '') {
    throw new Error(MALFORMED_SECRET_RESPONSE_ERROR);
  }
  if (typeof response.id !== 'string' || response.id.trim() === '') {
    throw new Error(MALFORMED_SECRET_RESPONSE_ERROR);
  }
  return { key: response.key, id: response.id };
}

interface ApiKeysPageProps {
  embedded?: boolean;
}

export function ApiKeysPage({ embedded = false }: ApiKeysPageProps) {
  const { user } = useAuth();
  const queryClient = useQueryClient();
  const [newKeyName, setNewKeyName] = useState('');
  const [newKeyPurpose, setNewKeyPurpose] = useState<ApiKeyPurpose>('OctoPrint');
  const [newKeyScopes, setNewKeyScopes] = useState<ApiKeyScope[]>([]);
  const [newKeyExpiresAt, setNewKeyExpiresAt] = useState('');
  const [showCreateForm, setShowCreateForm] = useState(false);
  const [createdKey, setCreatedKey] = useState<RevealedSecret | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<CreateKeyFieldErrors>({});
  const [revealedKeys, setRevealedKeys] = useState<Record<string, string>>({});
  const [isSecretLocked, setIsSecretLocked] = useState(false);

  const userId = user?.id;

  // Single lock covering the entire lifecycle of a secret-generating operation: from the
  // instant Create/Rotate is invoked, through the mutation's flight, through the one-time
  // secret being displayed, until it is explicitly dismissed (or the operation errors out).
  // Held as a plain ref — not just React state — so the create/rotate handlers can enforce
  // it synchronously and reject programmatic/double-click races regardless of when React
  // re-renders or when TanStack Query's mutation observers notify subscribers.
  const secretLockRef = useRef(false);

  const lockSecretOperation = () => {
    secretLockRef.current = true;
    setIsSecretLocked(true);
  };

  const unlockSecretOperation = () => {
    secretLockRef.current = false;
    setIsSecretLocked(false);
  };

  // Focus management for the strictly one-time secret panel: move focus to it the instant
  // it appears (so keyboard/screen-reader users are taken straight to the secret and its
  // aria-live announcement), and restore focus once it is dismissed. Restoration re-resolves
  // the trigger by stable identity (apiKey id / 'create') rather than trusting a possibly
  // detached document.activeElement, so it stays correct even if the row/button remounts or
  // the rotated key is removed before Done is pressed. Falls back deterministically — never
  // leaving focus on <body>.
  const createdKeyPanelRef = useRef<HTMLDivElement | null>(null);
  const createButtonRef = useRef<HTMLButtonElement | null>(null);
  const pageHeadingRef = useRef<HTMLHeadingElement | null>(null);
  const rotateButtonRefs = useRef(new Map<string, HTMLButtonElement>());
  const nameInputRef = useRef<HTMLInputElement | null>(null);
  const scopesGroupRef = useRef<HTMLFieldSetElement | null>(null);
  const expiryInputRef = useRef<HTMLInputElement | null>(null);
  const focusRestoreRef = useRef<FocusRestoreTarget | null>(null);
  const wasCreatedKeyShownRef = useRef(false);

  useEffect(() => {
    if (createdKey) {
      wasCreatedKeyShownRef.current = true;
      createdKeyPanelRef.current?.focus();
      return;
    }
    if (!wasCreatedKeyShownRef.current) {
      return;
    }
    wasCreatedKeyShownRef.current = false;

    const target = focusRestoreRef.current;
    focusRestoreRef.current = null;

    const isUsable = (el: HTMLButtonElement | null | undefined): el is HTMLButtonElement =>
      !!el && document.contains(el) && !el.disabled;

    if (target?.type === 'rotate') {
      const rotateButton = rotateButtonRefs.current.get(target.keyId);
      if (isUsable(rotateButton)) {
        rotateButton.focus();
        return;
      }
    }

    if (isUsable(createButtonRef.current)) {
      createButtonRef.current?.focus();
      return;
    }

    // Deterministic last-resort fallback: the row/control that triggered the secret is gone
    // (revoked/remounted) and the Create button is unavailable (e.g. the form is open).
    pageHeadingRef.current?.focus();
  }, [createdKey]);

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

  // Create API key mutation. The mutationFn itself validates the one-time secret and moves
  // it directly into transient component state, then returns void so the raw secret can
  // never become mutation.state.data — not even briefly — and therefore can never leak into
  // the React Query mutation cache.
  const createMutation = useMutation({
    mutationFn: async (request: CreateApiKeyRequest) => {
      if (!userId) throw new Error('User ID required');
      const response = await createApiKey(userId, request);
      const secret = extractOneTimeSecret(response);
      setCreatedKey(secret);
    },
    onSuccess: () => {
      setNewKeyName('');
      setNewKeyPurpose('OctoPrint');
      setNewKeyScopes([]);
      setNewKeyExpiresAt('');
      setFieldErrors({});
      setShowCreateForm(false);
      setError(null);
      queryClient.invalidateQueries({ queryKey: ['apiKeys', userId] });
    },
    onError: (err) => {
      unlockSecretOperation();
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

  // Rotate API key mutation. Same secrecy contract as create: the mutationFn validates and
  // moves the secret into transient state directly, returning void so it never touches the
  // mutation cache.
  const rotateMutation = useMutation({
    mutationFn: async ({ keyId }: { keyId: string }) => {
      if (!userId) throw new Error('User ID required');
      const response = await rotateApiKey(userId, keyId);
      const secret = extractOneTimeSecret(response);
      setCreatedKey(secret);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['apiKeys', userId] });
    },
    onError: (err) => {
      unlockSecretOperation();
      setError(err instanceof Error ? err.message : 'Failed to rotate API key');
    },
  });

  const validateAndFocus = (): boolean => {
    const errors: CreateKeyFieldErrors = {};

    if (!newKeyName.trim()) {
      errors.name = 'Enter a name for this API key.';
    }
    if (newKeyPurpose === 'Desktop' && newKeyScopes.length === 0) {
      errors.scopes = 'Select at least one scope for this Desktop-purpose key.';
    }
    if (newKeyExpiresAt) {
      const expiry = new Date(newKeyExpiresAt);
      const now = new Date();
      if (Number.isNaN(expiry.getTime()) || expiry <= now) {
        errors.expiry = 'Choose a date and time in the future.';
      } else if (expiry.getTime() > now.getTime() + MAX_KEY_LIFETIME_MS) {
        errors.expiry = 'Choose a date no more than 365 days from now.';
      }
    }

    setFieldErrors(errors);

    // Focus the first invalid field/group, following DOM order (name, then scopes, then
    // expiry) so combined errors are handled predictably.
    if (errors.name) {
      nameInputRef.current?.focus();
    } else if (errors.scopes) {
      scopesGroupRef.current?.focus();
    } else if (errors.expiry) {
      expiryInputRef.current?.focus();
    }

    return Object.keys(errors).length === 0;
  };

  const handleCreate = () => {
    // Enforced here (not only via disabled UI) so a second programmatic/double-click call
    // cannot start a create while create/rotate is pending or a secret is on screen.
    if (secretLockRef.current) return;
    if (!validateAndFocus()) return;

    lockSecretOperation();
    focusRestoreRef.current = { type: 'create' };
    createMutation.mutate({
      name: newKeyName.trim(),
      purpose: newKeyPurpose,
      scopeNames: newKeyPurpose === 'Desktop' ? newKeyScopes : undefined,
      expiresAt: newKeyExpiresAt ? new Date(newKeyExpiresAt).toISOString() : undefined,
    });
  };

  const toggleScope = (scope: ApiKeyScope) => {
    setNewKeyScopes((prev) => {
      const next = prev.includes(scope) ? prev.filter((s) => s !== scope) : [...prev, scope];
      if (next.length > 0) {
        setFieldErrors((prevErrors) => (prevErrors.scopes ? { ...prevErrors, scopes: undefined } : prevErrors));
      }
      return next;
    });
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
    // Enforced here (not only via disabled UI) so rotate cannot start while create/rotate
    // is pending or a secret is currently on screen.
    if (secretLockRef.current) return;
    if (!confirm(`Are you sure you want to rotate API key "${keyName}"? The old key will stop working immediately.`)) {
      return;
    }
    lockSecretOperation();
    focusRestoreRef.current = { type: 'rotate', keyId };
    rotateMutation.mutate({ keyId });
  };

  const handleDismissSecret = () => {
    setCreatedKey(null);
    unlockSecretOperation();
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

  const copyToClipboard = async (text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      toast.success('API key copied to clipboard');
    } catch {
      toast.error('Could not copy API key automatically. Please select and copy it manually.');
    }
  };

  const formatDate = (dateString: string) => {
    return new Date(dateString).toLocaleString();
  };

  const content = (
    <div className="space-y-6">
        {/* Information Banner */}
        <div className="bg-pf-info/10 border border-pf-info rounded-lg p-4">
          <h3 className="font-semibold text-pf-text-primary mb-2">What are API Keys?</h3>
          <p className="text-pf-text-secondary text-sm">
            OctoPrint keys let slicers upload G-code. Desktop keys are separate credentials with explicit model and
            library scopes. A key created for one purpose cannot be used for the other.
          </p>
          <p className="text-pf-text-secondary text-sm mt-2">
            <strong>Security:</strong> Treat API keys like passwords. Don't share them or commit them to version control.
          </p>
        </div>

        {/* Created Key Display */}
        {createdKey && (
          <div
            ref={createdKeyPanelRef}
            tabIndex={-1}
            className="bg-pf-success/10 border border-pf-success rounded-lg p-4 focus:outline-hidden focus:ring-2 focus:ring-pf-success focus:ring-offset-2 focus:ring-offset-pf-bg-0"
            role="status"
            aria-live="polite"
            aria-labelledby="created-key-heading"
            aria-describedby="created-key-warning created-key-value"
          >
            <h3 id="created-key-heading" className="font-semibold text-pf-success mb-2">API Key Created Successfully</h3>
            <p id="created-key-warning" className="text-pf-text-secondary text-sm mb-3">
              <strong className="text-pf-warning">Important:</strong> Copy this API key now. You won't be able to see it again!
            </p>
            <div id="created-key-value" className="bg-pf-bg-2 p-3 rounded-sm border border-pf-border font-mono text-sm break-all">
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
                onClick={handleDismissSecret}
              >
                Done
              </Button>
            </div>
          </div>
        )}

        {/* Error Display */}
        {(error || fetchError) && (
          <div className="bg-pf-error/10 border border-pf-error rounded-lg p-4 text-pf-error" role="alert">
            {error || (fetchError instanceof Error ? fetchError.message : 'Failed to load API keys')}
          </div>
        )}

        {/* Create New API Key */}
        <div className="bg-pf-bg-1 rounded-lg p-6 border border-pf-border">
          <div className="flex justify-between items-center mb-4">
            <h2
              id="api-keys-heading"
              ref={pageHeadingRef}
              tabIndex={-1}
              className="text-lg font-semibold text-pf-text-primary focus:outline-hidden focus:ring-2 focus:ring-pf-accent focus:ring-offset-2 focus:ring-offset-pf-bg-0 rounded-xs"
            >
              Your API Keys
            </h2>
            {!showCreateForm && (
              <Button
                variant="primary"
                ref={createButtonRef}
                onClick={() => setShowCreateForm(true)}
                disabled={isSecretLocked}
                iconLeft={<PlusIcon className="w-4 h-4" />}
              >
                Create New API Key
              </Button>
            )}
          </div>

          {showCreateForm && (
            <div className="mb-6 p-4 bg-pf-bg-2 rounded-sm border border-pf-border">
              <h3 className="font-semibold text-pf-text-primary mb-3">Create New API Key</h3>
              <div className="space-y-4">
                <FormField label="Name" htmlFor="apikey-name" required error={fieldErrors.name} errorId="apikey-name-error">
                  <Input
                    id="apikey-name"
                    ref={nameInputRef}
                    type="text"
                    value={newKeyName}
                    onChange={(e) => {
                      setNewKeyName(e.target.value);
                      setFieldErrors((prev) => (prev.name ? { ...prev, name: undefined } : prev));
                    }}
                    placeholder="Enter a descriptive name (e.g., 'PrusaSlicer Workstation')"
                    maxLength={256}
                    aria-required="true"
                    aria-invalid={!!fieldErrors.name}
                    aria-describedby={fieldErrors.name ? 'apikey-name-error' : undefined}
                  />
                </FormField>

                <FormField label="Purpose" htmlFor="apikey-purpose" helper="Desktop keys require explicit scopes and expire automatically; OctoPrint and legacy keys never gain desktop access.">
                  <Select
                    id="apikey-purpose"
                    value={newKeyPurpose}
                    onChange={(e) => {
                      setNewKeyPurpose(e.target.value as ApiKeyPurpose);
                      setNewKeyScopes([]);
                      setNewKeyExpiresAt('');
                      setFieldErrors((prev) => ({ ...prev, scopes: undefined, expiry: undefined }));
                    }}
                  >
                    <option value="OctoPrint">OctoPrint (compatible slicer uploads)</option>
                    <option value="Desktop">Desktop (PrintFarmer Desktop app)</option>
                  </Select>
                </FormField>

                {newKeyPurpose === 'Desktop' && (
                  <fieldset
                    ref={scopesGroupRef}
                    tabIndex={-1}
                    aria-invalid={!!fieldErrors.scopes}
                    aria-describedby={fieldErrors.scopes ? 'apikey-scopes-helper apikey-scopes-error' : 'apikey-scopes-helper'}
                    className="space-y-2 rounded-xs focus:outline-hidden focus:ring-2 focus:ring-pf-error focus:ring-offset-2 focus:ring-offset-pf-bg-0"
                  >
                    <legend className="text-sm font-medium text-pf-text-primary">
                      Scopes <span className="text-pf-error" aria-hidden="true">*</span>
                    </legend>
                    <p id="apikey-scopes-helper" className="text-xs text-pf-text-muted">
                      Select only what this key needs. Each option grants exactly one permission, and the
                      server will reject any scope the key&apos;s owner is not already authorized for —
                      today that means calibration, slicing, and queue scopes require a farm admin owner.
                    </p>
                    {fieldErrors.scopes && (
                      <p id="apikey-scopes-error" role="alert" className="text-xs text-pf-error-text">
                        {fieldErrors.scopes}
                      </p>
                    )}
                    <div className="space-y-4">
                      {SCOPE_GROUPS.map((group) => (
                        <div key={group.id} className="space-y-2">
                          <p className="text-xs font-semibold uppercase tracking-wide text-pf-text-secondary">
                            {group.title}
                          </p>
                          <p className="text-xs text-pf-text-muted">{group.description}</p>
                          <div className="space-y-2">
                            {group.options.map((scope) => (
                              <Checkbox
                                key={scope.value}
                                id={`scope-${scope.value}`}
                                checked={newKeyScopes.includes(scope.value)}
                                onChange={() => toggleScope(scope.value)}
                                label={
                                  scope.impact
                                    ? `${scope.label} — ${scope.description} ${scope.impact}`
                                    : `${scope.label} — ${scope.description}`
                                }
                              />
                            ))}
                          </div>
                        </div>
                      ))}
                    </div>
                  </fieldset>
                )}

                <FormField
                  label="Expires At"
                  htmlFor="apikey-expiry"
                  helper={newKeyPurpose === 'Desktop'
                    ? 'Optional. Defaults to 90 days from creation if left blank (max 365 days).'
                    : 'Optional. OctoPrint keys do not expire when this is left blank (max 365 days).'}
                  helperId="apikey-expiry-helper"
                  error={fieldErrors.expiry}
                  errorId="apikey-expiry-error"
                >
                  <Input
                    id="apikey-expiry"
                    ref={expiryInputRef}
                    type="datetime-local"
                    value={newKeyExpiresAt}
                    onChange={(e) => {
                      setNewKeyExpiresAt(e.target.value);
                      setFieldErrors((prev) => (prev.expiry ? { ...prev, expiry: undefined } : prev));
                    }}
                    aria-invalid={!!fieldErrors.expiry}
                    aria-describedby={fieldErrors.expiry ? 'apikey-expiry-error' : 'apikey-expiry-helper'}
                  />
                </FormField>

                <div className="flex gap-2">
                  <Button
                    variant="primary"
                    onClick={handleCreate}
                    disabled={isSecretLocked}
                  >
                    {createMutation.isPending ? 'Creating...' : 'Create'}
                  </Button>
                  <Button
                    variant="secondary"
                    onClick={() => {
                      setShowCreateForm(false);
                      setNewKeyName('');
                      setNewKeyPurpose('OctoPrint');
                      setNewKeyScopes([]);
                      setNewKeyExpiresAt('');
                      setError(null);
                      setFieldErrors({});
                    }}
                  >
                    Cancel
                  </Button>
                </div>
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
                      <div className="flex items-center gap-3 flex-wrap">
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
                        <Badge variant={apiKey.purpose === 'Desktop' ? 'primary' : 'default'} size="sm">
                          {apiKey.purpose === 'Desktop' ? 'Desktop' : 'OctoPrint'}
                        </Badge>
                        {apiKey.isExpired && (
                          <Badge variant="error" size="sm">
                            Expired
                          </Badge>
                        )}
                      </div>
                      <p className="text-sm text-pf-text-secondary mt-1">
                        Created: {formatDate(apiKey.createdAt)}
                        {apiKey.expiresAt && ` • Expires: ${formatDate(apiKey.expiresAt)}`}
                      </p>
                      {apiKey.purpose === 'Desktop' && resolveScopeNames(apiKey).length > 0 && (
                        <p className="text-sm text-pf-text-secondary mt-1">
                          Scopes: {resolveScopeNames(apiKey).join(', ')}
                        </p>
                      )}
                    </div>
                    <div className="flex gap-2 items-center">
                      <Toggle
                        checked={apiKey.isActive}
                        onChange={() => handleToggle(apiKey.id)}
                        disabled={toggleMutation.isPending}
                        size="sm"
                        aria-label={`${apiKey.isActive ? 'Disable' : 'Enable'} API key ${apiKey.name}`}
                      />
                      {canRevealKeys && apiKey.purpose === 'OctoPrint' && (
                        <Button
                          variant="secondary"
                          onClick={() => handleReveal(apiKey.id)}
                          iconLeft={revealedKeys[apiKey.id] ? <EyeOffIcon className="w-4 h-4" /> : <EyeIcon className="w-4 h-4" />}
                          title={revealedKeys[apiKey.id] ? 'Hide API key' : 'Reveal API key'}
                          aria-label={`${revealedKeys[apiKey.id] ? 'Hide' : 'Reveal'} API key ${apiKey.name}`}
                        />
                      )}
                      <Button
                        ref={(el) => {
                          if (!el) return;
                          rotateButtonRefs.current.set(apiKey.id, el);
                          return () => {
                            rotateButtonRefs.current.delete(apiKey.id);
                          };
                        }}
                        variant="secondary"
                        onClick={() => handleRotate(apiKey.id, apiKey.name)}
                        disabled={isSecretLocked || apiKey.isExpired}
                        iconLeft={<RefreshIcon className="w-4 h-4" />}
                        title={apiKey.isExpired ? 'Expired API keys cannot be rotated' : 'Rotate (generate new key)'}
                        aria-label={`Rotate API key ${apiKey.name}`}
                      />
                      <Button
                        variant="danger"
                        onClick={() => handleDelete(apiKey.id, apiKey.name)}
                        disabled={deleteMutation.isPending}
                        iconLeft={<DeleteIcon className="w-4 h-4" />}
                        title="Delete"
                        aria-label={`Delete API key ${apiKey.name}`}
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
            See the <a href="/docs/SLICER_CONFIGURATION.md" className="text-pf-accent hover:underline">Slicer Configuration Guide</a> for
            step-by-step instructions on configuring PrusaSlicer, OrcaSlicer, and other slicers to work with PrintFarmer.
          </p>
        </div>
      </div>
  );

  return (
    <PageTemplate
      title="API Keys"
      subtitle="Manage purpose-limited credentials for slicers and PrintFarmer Desktop"
      icon={KeyIcon}
      embedded={embedded}
    >
      {content}
    </PageTemplate>
  );
}
