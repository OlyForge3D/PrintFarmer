import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Alert, Button, Card, FormField, Input, Toggle } from '@/common/components/ui';
import { saveSpoolmanConfig } from '@/services/api/setupApi';
import {
  fetchHomeAssistantSettings,
  fetchSpoolmanSettings,
  saveHomeAssistantSettings,
  type HomeAssistantSettings,
} from '@/services/api/integrationSettingsApi';

const spoolmanKey = ['spoolman-config'] as const;
const homeAssistantKey = ['home-assistant-settings'] as const;

export function SpoolmanSettingsCard() {
  const queryClient = useQueryClient();
  const query = useQuery({ queryKey: spoolmanKey, queryFn: fetchSpoolmanSettings });
  const save = useMutation({
    mutationFn: (baseUrl: string) => saveSpoolmanConfig({ baseUrl }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: spoolmanKey });
      toast.success('Spoolman settings saved');
    },
    onError: (error: Error) => toast.error(`Failed to save Spoolman settings: ${error.message}`),
  });
  if (query.isPending) return <p role="status">Loading Spoolman settings...</p>;
  if (query.isError) return <Alert type="error" title="Unable to load Spoolman settings">{query.error.message}</Alert>;
  return <SpoolmanForm baseUrl={query.data.baseUrl} saving={save.isPending} onSave={(url) => save.mutate(url)} />;
}

function SpoolmanForm({ baseUrl, saving, onSave }: { baseUrl: string; saving: boolean; onSave: (url: string) => void }) {
  const [url, setUrl] = useState(baseUrl);
  return (
    <Card>
      <Card.Body>
        <form className="space-y-4" onSubmit={(event) => { event.preventDefault(); onSave(url.trim()); }}>
          <h3 className="text-lg font-semibold text-pf-text-primary">Spoolman</h3>
          <FormField label="Spoolman URL" htmlFor="spoolman-url">
            <Input id="spoolman-url" type="url" value={url} onChange={(event) => setUrl(event.target.value)} />
          </FormField>
          <Button type="submit" loading={saving}>Save Spoolman Settings</Button>
        </form>
      </Card.Body>
    </Card>
  );
}

export function HomeAssistantSettingsCard() {
  const queryClient = useQueryClient();
  const query = useQuery({ queryKey: homeAssistantKey, queryFn: fetchHomeAssistantSettings });
  const save = useMutation({
    mutationFn: saveHomeAssistantSettings,
    onSuccess: (settings) => {
      queryClient.setQueryData(homeAssistantKey, settings);
      toast.success('Home Assistant settings saved');
    },
    onError: (error: Error) => toast.error(`Failed to save Home Assistant settings: ${error.message}`),
  });
  if (query.isPending) return <p role="status">Loading Home Assistant settings...</p>;
  if (query.isError) return <Alert type="error" title="Unable to load Home Assistant settings">{query.error.message}</Alert>;
  return <HomeAssistantForm settings={query.data} saving={save.isPending} onSave={save.mutate} />;
}

function HomeAssistantForm({ settings, saving, onSave }: {
  settings: HomeAssistantSettings;
  saving: boolean;
  onSave: (settings: Parameters<typeof saveHomeAssistantSettings>[0]) => void;
}) {
  const [enabled, setEnabled] = useState(settings.enabled);
  const [baseUrl, setBaseUrl] = useState(settings.baseUrl);
  const [token, setToken] = useState('');
  return (
    <Card>
      <Card.Body>
        <form className="space-y-4" onSubmit={(event) => {
          event.preventDefault();
          onSave({ enabled, baseUrl: baseUrl.trim(), token });
        }}>
          <h3 className="text-lg font-semibold text-pf-text-primary">Home Assistant</h3>
          <FormField label="Enable Home Assistant">
            <Toggle checked={enabled} onChange={(event) => setEnabled(event.target.checked)} aria-label="Enable Home Assistant" />
          </FormField>
          <FormField label="Home Assistant URL" htmlFor="home-assistant-url">
            <Input id="home-assistant-url" type="url" required={enabled} value={baseUrl} onChange={(event) => setBaseUrl(event.target.value)} />
          </FormField>
          <FormField label="Access token" htmlFor="home-assistant-token" helper="Leave blank to keep the stored token.">
            <Input id="home-assistant-token" type="password" autoComplete="new-password" required={enabled && !settings.tokenMasked} value={token} onChange={(event) => setToken(event.target.value)} />
          </FormField>
          <Button type="submit" loading={saving}>Save Home Assistant Settings</Button>
        </form>
      </Card.Body>
    </Card>
  );
}
