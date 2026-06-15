import { useState } from 'react';
import { toast } from 'sonner';
import { AlertCircleIcon } from '@/common/components/icons/MdiIcons';
import { Skeleton } from '@/common/components/skeletons/Skeleton';
import { Alert, Button, Card, FormField, Input, Select } from '@/common/components/ui';
import { useUserSettings, useUpdateUserSettings } from '@/features/settings/hooks/useUserSettings';
import type { UserSettingsResponse } from '@/features/settings/types';
import type { ApiError } from '@/types/api';

const LOCALE_OPTIONS = [
  { value: 'en', label: 'English' },
  { value: 'de', label: 'Deutsch' },
  { value: 'fr', label: 'Français' },
  { value: 'es', label: 'Español' },
];

const PRINTABLES_USERNAME_AT_PREFIX_ERROR = "Printables username must not begin with '@'.";

export function UserSettingsSection() {
  const { data, isLoading, error, refetch, isFetching } = useUserSettings();
  const mutation = useUpdateUserSettings();

  if (isLoading) {
    return <UserSettingsSkeleton />;
  }

  if (error) {
    return (
      <Alert type="error" title="Unable to load user preferences">
        <div className="flex items-start gap-3">
          <AlertCircleIcon className="mt-0.5 h-5 w-5 shrink-0" ariaLabel="Error" />
          <div className="space-y-3">
            <p>Your preferences could not be loaded right now.</p>
            <Button type="button" variant="secondary" size="sm" loading={isFetching} onClick={() => void refetch()}>
              Retry
            </Button>
          </div>
        </div>
      </Alert>
    );
  }

  if (!data) {
    return null;
  }

  return <UserSettingsForm data={data} mutation={mutation} />;
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

function UserSettingsForm({
  data,
  mutation,
}: {
  data: UserSettingsResponse;
  mutation: ReturnType<typeof useUpdateUserSettings>;
}) {
  const [locale, setLocale] = useState(data.locale);
  const [itemsPerPage, setItemsPerPage] = useState(String(data.itemsPerPage));
  const [printablesUsername, setPrintablesUsername] = useState(data.printablesUsername ?? '');

  const handleSave = () => {
    const items = Number(itemsPerPage);
    if (items < 1 || items > 200) {
      toast.error('Items per page must be between 1 and 200.');
      return;
    }

    const normalizedPrintablesUsername = printablesUsername.trim();
    if (normalizedPrintablesUsername.length > 64) {
      toast.error('Printables username must be 64 characters or fewer.');
      return;
    }
    if (normalizedPrintablesUsername.startsWith('@')) {
      toast.error(PRINTABLES_USERNAME_AT_PREFIX_ERROR);
      return;
    }

    mutation.mutate(
      {
        theme: data.theme,
        locale,
        itemsPerPage: items,
        defaultSlicerPreset: data.defaultSlicerPreset ?? null,
        printablesUsername: normalizedPrintablesUsername === '' ? '' : normalizedPrintablesUsername,
        rowVersion: data.rowVersion,
      },
      {
        onSuccess: () => toast.success('Preferences saved.'),
        onError: (error) => {
          const apiError = error as Partial<ApiError>;
          if (apiError.statusCode === 409) {
            return;
          }

          const combinedMessage = `${apiError.message ?? ''} ${apiError.details ?? ''}`.toLowerCase();
          if (
            combinedMessage.includes('printables')
            && combinedMessage.includes('username')
            && combinedMessage.includes('must not begin')
          ) {
            toast.error(PRINTABLES_USERNAME_AT_PREFIX_ERROR);
            return;
          }

          const fallbackMessage = apiError.message ?? apiError.details;
          if (fallbackMessage) {
            toast.error(fallbackMessage);
          }
        },
      },
    );
  };

  return (
    <Card>
      <Card.Header>
        <h2 className="text-lg font-semibold text-pf-text-primary">User Preferences</h2>
        <p className="mt-1 text-sm text-pf-text-secondary">
          Personal settings that apply only to your account.
        </p>
      </Card.Header>
      <Card.Body>
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <FormField label="Locale">
            <Select value={locale} onChange={(e) => setLocale(e.target.value)} aria-label="Locale">
              {LOCALE_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </Select>
          </FormField>
          <FormField label="Items Per Page">
            <Input
              type="number"
              min={1}
              max={200}
              value={itemsPerPage}
              onChange={(e) => setItemsPerPage(e.target.value)}
              aria-label="Items per page"
            />
          </FormField>
          <FormField label="Printables Username">
            <Input
              type="text"
              maxLength={64}
              value={printablesUsername}
              onChange={(e) => setPrintablesUsername(e.target.value)}
              placeholder="Optional"
              aria-label="Printables username"
            />
          </FormField>
        </div>
      </Card.Body>
      <Card.Footer>
        <div className="flex justify-end">
          <Button variant="primary" onClick={handleSave} disabled={mutation.isPending}>
            {mutation.isPending ? 'Saving...' : 'Save Preferences'}
          </Button>
        </div>
      </Card.Footer>
    </Card>
  );
}
