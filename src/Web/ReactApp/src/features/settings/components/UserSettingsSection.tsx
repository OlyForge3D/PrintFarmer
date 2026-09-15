import { useId, useRef, useState, type FormEvent } from 'react';
import { useIsMutating, useQueryClient, type MutationFilters } from '@tanstack/react-query';
import { getAuthEpoch } from '@/common/auth/authEpoch';
import { toast } from 'sonner';
import { AlertCircleIcon } from '@/common/components/icons/MdiIcons';
import { Skeleton } from '@/common/components/skeletons/Skeleton';
import { Alert, Button, Card, FormField, Input, Radio, Select } from '@/common/components/ui';
import { USER_SETTINGS_KEY, useUserSettings, useUpdateUserSettings } from '@/features/settings/hooks/useUserSettings';
import type { PrinterControlMode, UserSettingsResponse } from '@/features/settings/types';
import type { ApiError } from '@/types/api';

const LOCALE_OPTIONS = [
  { value: 'en', label: 'English' },
  { value: 'de', label: 'Deutsch' },
  { value: 'fr', label: 'Français' },
  { value: 'es', label: 'Español' },
];

// Keep the render-time and synchronous guards aligned. An unset context is the
// brief window before onMutate runs, so it must still block overlapping saves.
const accountSaveFilters: MutationFilters = {
  mutationKey: USER_SETTINGS_KEY,
  predicate: mutation => {
    const context = mutation.state.context as { epochAtStart: number } | undefined;
    return !context || context.epochAtStart === getAuthEpoch();
  },
};

const PRINTABLES_USERNAME_AT_PREFIX_ERROR = "Printables username must not begin with '@'.";

export function UserSettingsSection() {
  const { data, isLoading, error, refetch, isFetching } = useUserSettings();
  const mutation = useUpdateUserSettings();

  if (isLoading || (!data && !error)) {
    return <div role="status" aria-label="Loading user preferences"><UserSettingsSkeleton /></div>;
  }

  const loadError = error ? (
    <Alert type="error" title="Unable to load user preferences">
      <div className="flex items-start gap-3">
        <AlertCircleIcon className="mt-0.5 h-5 w-5 shrink-0" ariaLabel="Error" />
        <div className="space-y-3">
          <p>Your preferences could not be loaded right now. Retry before saving.</p>
          <Button type="button" variant="secondary" size="sm" loading={isFetching} onClick={() => void refetch()}>
            Retry
          </Button>
        </div>
      </div>
    </Alert>
  ) : null;

  return (
    <div className="space-y-4">
      {loadError}
      {data && (
        <UserSettingsForm
          key={data.userId}
          data={data}
          mutation={mutation}
          refetch={refetch}
          unavailable={Boolean(error)}
          refreshing={isFetching}
        />
      )}
    </div>
  );
}

function UserSettingsSkeleton() {
  return (
    <Card>
      <Card.Header>
        <Skeleton width="30%" />
        <Skeleton width="50%" />
      </Card.Header>
      <Card.Body>
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          {Array.from({ length: 2 }).map((_, index) => (
            <div key={`user-settings-skeleton-${index}`} className="space-y-2">
              <Skeleton width="48%" />
              <Skeleton height={40} />
            </div>
          ))}
        </div>
      </Card.Body>
      <Card.Footer>
        <div className="flex justify-end">
          <Skeleton width="140px" height={40} />
        </div>
      </Card.Footer>
    </Card>
  );
}

interface PreferencesDraft {
  locale?: string;
  itemsPerPage?: string;
  printablesUsername?: string;
  printerControlMode?: PrinterControlMode;
}

function UserSettingsForm({
  data,
  mutation,
  refetch,
  unavailable,
  refreshing,
}: {
  data: UserSettingsResponse;
  mutation: ReturnType<typeof useUpdateUserSettings>;
  refetch: ReturnType<typeof useUserSettings>['refetch'];
  unavailable: boolean;
  refreshing: boolean;
}) {
  const id = useId();
  const queryClient = useQueryClient();
  const accountSavePending = useIsMutating(accountSaveFilters) > 0;
  const submitting = useRef(false);
  const itemsRef = useRef<HTMLInputElement>(null);
  const printablesRef = useRef<HTMLInputElement>(null);
  const [invalidField, setInvalidField] = useState<'items' | 'printables' | null>(null);
  // Keep only edits locally: untouched fields always reflect the shared account cache.
  const [draft, setDraft] = useState<PreferencesDraft>({});
  const [draftRevision, setDraftRevision] = useState(data.rowVersion);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [conflict, setConflict] = useState(false);
  const [reloading, setReloading] = useState(false);
  const locale = draft.locale ?? data.locale;
  const itemsPerPage = draft.itemsPerPage ?? String(data.itemsPerPage);
  const printablesUsername = draft.printablesUsername ?? data.printablesUsername ?? '';
  const printerControlMode = draft.printerControlMode ?? data.printerControlMode ?? 'Guided';
  const busy = mutation.isPending || accountSavePending || reloading;
  const changedElsewhere = Object.keys(draft).length > 0 && draftRevision !== data.rowVersion;

  const edit = (change: PreferencesDraft) => {
    setInvalidField(null);
    if (!conflict) setSaveError(null);
    if (Object.keys(draft).length === 0) setDraftRevision(data.rowVersion);
    setDraft(previous => ({ ...previous, ...change }));
  };

  const reloadLatest = async () => {
    setReloading(true);
    try {
      const result = await refetch();
      if (result.error || !result.data) {
        setSaveError('Could not reload preferences. Your edits are still here; try reloading again.');
        return;
      }
      setDraft({});
      setInvalidField(null);
      setConflict(false);
      setSaveError(null);
    } catch {
      setSaveError('Could not reload preferences. Your edits are still here; try reloading again.');
    } finally {
      setReloading(false);
    }
  };

  const handleSave = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    // Check the cache synchronously too, before React has rendered pending state.
    if (submitting.current || busy || unavailable || refreshing || conflict
      || queryClient.isMutating(accountSaveFilters) > 0) return;

    const failValidation = (message: string, field: 'items' | 'printables') => {
      setInvalidField(field);
      (field === 'items' ? itemsRef : printablesRef).current?.focus();
      setSaveError(message);
      toast.error(message);
    };
    const items = Number(itemsPerPage);
    if (!Number.isInteger(items) || items < 1 || items > 200) {
      failValidation('Items per page must be a whole number between 1 and 200.', 'items');
      return;
    }
    const normalizedPrintablesUsername = printablesUsername.trim();
    if (normalizedPrintablesUsername.length > 64) {
      failValidation('Printables username must be 64 characters or fewer.', 'printables');
      return;
    }
    if (normalizedPrintablesUsername.startsWith('@')) {
      failValidation(PRINTABLES_USERNAME_AT_PREFIX_ERROR, 'printables');
      return;
    }

    submitting.current = true;
    setSaveError(null);
    setInvalidField(null);
    mutation.mutate(
      {
        theme: data.theme,
        printerControlMode,
        locale,
        itemsPerPage: items,
        defaultSlicerPreset: data.defaultSlicerPreset ?? null,
        printablesUsername: normalizedPrintablesUsername,
        rowVersion: data.rowVersion,
      },
      {
        onSuccess: () => {
          setDraft({});
          toast.success('Preferences saved.');
        },
        onError: (error) => {
          const apiError = error as Partial<ApiError>;
          if (apiError.statusCode === 409) {
            setConflict(true);
            setSaveError('Preferences changed elsewhere. Your edits have not been saved. Reload the latest preferences to review them and choose your changes again.');
            return;
          }
          const combinedMessage = `${apiError.message ?? ''} ${apiError.details ?? ''}`.toLowerCase();
          const message = combinedMessage.includes('printables')
            && combinedMessage.includes('username') && combinedMessage.includes('must not begin')
            ? PRINTABLES_USERNAME_AT_PREFIX_ERROR
            : apiError.message || apiError.details || 'Could not save preferences. Your edits are still here; try saving again.';
          setSaveError(message);
          toast.error(message);
        },
        onSettled: () => { submitting.current = false; },
      },
    );
  };

  return (
    <form onSubmit={handleSave} noValidate aria-label="User preferences" aria-busy={busy}>
      <Card>
        <Card.Header>
          <h3 className="text-lg font-semibold text-pf-text-primary">User Preferences</h3>
          <p className="mt-1 text-sm font-normal text-pf-text-secondary">
            Personal settings that apply only to your account.
          </p>
        </Card.Header>
        <Card.Body>
          <div className="space-y-6">
            {saveError && (
              <Alert type="error" title={conflict ? 'Preferences need review' : 'Preferences not saved'}>
                <p id={`${id}-save-error`}>{saveError}</p>
                {conflict && (
                  <Button type="button" variant="secondary" className="mt-3" loading={reloading}
                    disabled={busy} onClick={() => void reloadLatest()}>
                    Reload latest preferences (discard edits)
                  </Button>
                )}
              </Alert>
            )}
            {changedElsewhere && !conflict && (
              <div role="status">
                <Alert type="info">
                  Preferences changed elsewhere. Your unsaved edits are kept; other fields show the latest values. Review before saving.
                </Alert>
              </div>
            )}
            <fieldset disabled={busy || unavailable} className="min-w-0 space-y-6">
              <fieldset className="min-w-0" aria-describedby={`${id}-mode-help`}>
                <legend className="text-base font-semibold text-pf-text-primary">Printer control mode</legend>
                <p id={`${id}-mode-help`} className="mt-1 max-w-prose text-sm text-pf-text-secondary">
                  Guided shows printer-motion help and hints; Expert collapses them. This account preference is shared by printer detail and sidebar controls. It does not change permissions or protections.
                </p>
                <div className="mt-3 flex flex-wrap gap-x-6 gap-y-2">
                  <Radio id={`${id}-guided`} name={`${id}-mode`} label="Guided" value="Guided"
                    className="my-4" checked={printerControlMode === 'Guided'} onChange={() => edit({ printerControlMode: 'Guided' })} />
                  <Radio id={`${id}-expert`} name={`${id}-mode`} label="Expert" value="Expert"
                    className="my-4" checked={printerControlMode === 'Expert'} onChange={() => edit({ printerControlMode: 'Expert' })} />
                </div>
              </fieldset>
              <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
                <FormField label="Locale" htmlFor={`${id}-locale`}>
                  <Select id={`${id}-locale`} value={locale} onChange={(e) => edit({ locale: e.target.value })}>
                    {LOCALE_OPTIONS.map((option) => (
                      <option key={option.value} value={option.value}>{option.label}</option>
                    ))}
                  </Select>
                </FormField>
                <FormField label="Items Per Page" htmlFor={`${id}-items`}>
                  <Input id={`${id}-items`} ref={itemsRef} aria-invalid={invalidField === 'items'}
                    aria-describedby={invalidField === 'items' ? `${id}-save-error` : undefined} type="number" min={1} max={200} step={1}
                    value={itemsPerPage} onChange={(e) => edit({ itemsPerPage: e.target.value })}
                    aria-label="Items per page" />
                </FormField>
                <FormField label="Printables Username" htmlFor={`${id}-printables`}>
                  <Input id={`${id}-printables`} ref={printablesRef} aria-invalid={invalidField === 'printables'}
                    aria-describedby={invalidField === 'printables' ? `${id}-save-error` : undefined} type="text" maxLength={64}
                    value={printablesUsername} onChange={(e) => edit({ printablesUsername: e.target.value })}
                    placeholder="Optional" aria-label="Printables username" />
                </FormField>
              </div>
            </fieldset>
          </div>
        </Card.Body>
        <Card.Footer>
          <div className="flex flex-wrap items-center justify-end gap-3">
            {accountSavePending && !mutation.isPending && <p role="status" className="text-sm text-pf-text-secondary">Saving account preferences…</p>}
            <Button type="submit" variant="primary" disabled={busy || unavailable || refreshing || conflict}>
              {mutation.isPending ? 'Saving...' : 'Save Preferences'}
            </Button>
          </div>
        </Card.Footer>
      </Card>
    </form>
  );
}
