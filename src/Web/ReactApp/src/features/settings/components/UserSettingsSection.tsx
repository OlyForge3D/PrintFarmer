import { useState } from 'react';
import { toast } from 'sonner';
import { Card, Button, Input, Select, FormField, Spinner } from '@/common/components/ui';
import { useUserSettings, useUpdateUserSettings } from '@/features/settings/hooks/useUserSettings';
import type { UserSettingsResponse } from '@/features/settings/types';

const THEME_OPTIONS = [
  { value: 'system', label: 'System' },
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
];

const LOCALE_OPTIONS = [
  { value: 'en', label: 'English' },
  { value: 'de', label: 'Deutsch' },
  { value: 'fr', label: 'Français' },
  { value: 'es', label: 'Español' },
];

export function UserSettingsSection() {
  const { data, isLoading, error } = useUserSettings();
  const mutation = useUpdateUserSettings();

  if (isLoading) return <Spinner className="mx-auto" />;
  if (error) return <div className="text-pf-error">Failed to load user preferences.</div>;
  if (!data) return null;

  return <UserSettingsForm data={data} mutation={mutation} />;
}

function UserSettingsForm({
  data,
  mutation,
}: {
  data: UserSettingsResponse;
  mutation: ReturnType<typeof useUpdateUserSettings>;
}) {
  const [theme, setTheme] = useState(data.theme);
  const [locale, setLocale] = useState(data.locale);
  const [itemsPerPage, setItemsPerPage] = useState(String(data.itemsPerPage));
  const [defaultSlicerPreset, setDefaultSlicerPreset] = useState(data.defaultSlicerPreset ?? '');

  const handleSave = () => {
    const items = Number(itemsPerPage);
    if (items < 1 || items > 200) {
      toast.error('Items per page must be between 1 and 200.');
      return;
    }

    mutation.mutate(
      {
        theme,
        locale,
        itemsPerPage: items,
        defaultSlicerPreset: defaultSlicerPreset || null,
        rowVersion: data.rowVersion,
      },
      {
        onSuccess: () => toast.success('Preferences saved.'),
      },
    );
  };

  return (
    <Card>
      <Card.Header>
        <h2 className="text-lg font-semibold text-pf-text-primary">User Preferences</h2>
        <p className="text-sm text-pf-text-secondary mt-1">
          Personal settings that apply only to your account.
        </p>
      </Card.Header>
      <Card.Body>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <FormField label="Theme">
            <Select
              value={theme}
              onChange={(e) => setTheme(e.target.value)}
              aria-label="Theme"
            >
              {THEME_OPTIONS.map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
                </option>
              ))}
            </Select>
          </FormField>
          <FormField label="Locale">
            <Select
              value={locale}
              onChange={(e) => setLocale(e.target.value)}
              aria-label="Locale"
            >
              {LOCALE_OPTIONS.map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
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
          <FormField label="Default Slicer Preset">
            <Input
              type="text"
              value={defaultSlicerPreset}
              onChange={(e) => setDefaultSlicerPreset(e.target.value)}
              placeholder="e.g. 0.20mm Standard"
              aria-label="Default slicer preset"
            />
          </FormField>
        </div>
      </Card.Body>
      <Card.Footer>
        <div className="flex justify-end">
          <Button
            variant="primary"
            onClick={handleSave}
            disabled={mutation.isPending}
          >
            {mutation.isPending ? 'Saving...' : 'Save Preferences'}
          </Button>
        </div>
      </Card.Footer>
    </Card>
  );
}
