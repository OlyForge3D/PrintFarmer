import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { EyeIcon, EyeOffIcon } from '@/common/components/icons/MdiIcons';
import { Alert, Button, Card, FormField, Input, Toggle } from '@/common/components/ui';
import { apiClient } from '@/services/api';
import type { TelegramSettingsDto, UpdateTelegramSettingsRequest } from '@/types/api';

const telegramSettingsQueryKey = ['telegram-settings'] as const;

export function TelegramSettingsCard() {
  const queryClient = useQueryClient();
  const { data, isLoading, error } = useQuery({
    queryKey: telegramSettingsQueryKey,
    queryFn: () => apiClient.getTelegramSettings(),
    staleTime: 30_000,
  });

  const updateMutation = useMutation({
    mutationFn: (request: UpdateTelegramSettingsRequest) => apiClient.updateTelegramSettings(request),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: telegramSettingsQueryKey });
      toast.success('Telegram settings saved');
    },
    onError: (err: Error) => toast.error(`Failed to save Telegram settings: ${err.message}`),
  });

  const testMutation = useMutation({
    mutationFn: () => apiClient.sendTelegramTestMessage(),
    onSuccess: (result) => {
      if (result.success) {
        toast.success(result.message);
      } else {
        toast.error(result.message);
      }
    },
    onError: (err: Error) => toast.error(`Failed to send Telegram test message: ${err.message}`),
  });

  if (isLoading) {
    return null;
  }

  if (error) {
    return <Alert type="warning">Unable to load Telegram notification settings.</Alert>;
  }

  return data ? (
    <TelegramSettingsForm
      settings={data}
      isSaving={updateMutation.isPending}
      isTesting={testMutation.isPending}
      onSave={(request) => updateMutation.mutate(request)}
      onTest={() => testMutation.mutate()}
    />
  ) : null;
}

interface TelegramSettingsFormProps {
  settings: TelegramSettingsDto;
  isSaving: boolean;
  isTesting: boolean;
  onSave: (request: UpdateTelegramSettingsRequest) => void;
  onTest: () => void;
}

function TelegramSettingsForm({
  settings,
  isSaving,
  isTesting,
  onSave,
  onTest,
}: TelegramSettingsFormProps) {
  const [enabled, setEnabled] = useState(settings.enabled);
  const [chatId, setChatId] = useState(settings.chatId);
  const [botToken, setBotToken] = useState(settings.botTokenMasked);
  const [includeSnapshots, setIncludeSnapshots] = useState(settings.includeSnapshots);
  const [showToken, setShowToken] = useState(false);

  useEffect(() => {
    setEnabled(settings.enabled);
    setChatId(settings.chatId);
    setBotToken(settings.botTokenMasked);
    setIncludeSnapshots(settings.includeSnapshots);
  }, [settings]);

  const handleSave = () => {
    if (enabled && !chatId.trim()) {
      toast.error('Telegram chat ID is required when Telegram is enabled.');
      return;
    }

    if (enabled && !botToken.trim()) {
      toast.error('Telegram bot token is required when Telegram is enabled.');
      return;
    }

    onSave({
      enabled,
      chatId: chatId.trim(),
      includeSnapshots,
      botToken: botToken.trim(),
    });
  };

  return (
    <Card>
      <Card.Header>
        <h3 className="text-lg font-semibold text-pf-text-primary">Telegram Notifications</h3>
        <p className="mt-1 text-sm text-pf-text-secondary">
          Send print event notifications to a Telegram chat using a farm-wide bot token.
        </p>
      </Card.Header>
      <Card.Body>
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <FormField label="Enable Telegram" helper="Users still opt in per event from notification preferences.">
            <Toggle
              checked={enabled}
              onChange={(event) => setEnabled(event.target.checked)}
              aria-label="Enable Telegram notifications"
            />
          </FormField>
          <FormField label="Attach camera snapshots" helper="When a printer snapshot is available, Telegram receives a photo.">
            <Toggle
              checked={includeSnapshots}
              onChange={(event) => setIncludeSnapshots(event.target.checked)}
              aria-label="Attach camera snapshots to Telegram notifications"
            />
          </FormField>
          <FormField label="Chat ID" required={enabled} htmlFor="telegram-chat-id">
            <Input
              id="telegram-chat-id"
              type="text"
              value={chatId}
              onChange={(event) => setChatId(event.target.value)}
              placeholder="123456789 or -1001234567890"
              aria-required={enabled}
            />
          </FormField>
          <FormField
            label="Bot token"
            required={enabled}
            htmlFor="telegram-bot-token"
            helper="Paste a new token to replace the stored secret. Masked values keep the current token."
          >
            <div className="flex gap-2">
              <Input
                id="telegram-bot-token"
                type={showToken ? 'text' : 'password'}
                value={botToken}
                onChange={(event) => setBotToken(event.target.value)}
                placeholder="123456:ABC..."
                aria-required={enabled}
              />
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={() => setShowToken((value) => !value)}
                aria-label={showToken ? 'Hide bot token' : 'Show bot token'}
                iconLeft={showToken ? <EyeOffIcon ariaLabel="Hide" /> : <EyeIcon ariaLabel="Show" />}
              >
                {showToken ? 'Hide' : 'Show'}
              </Button>
            </div>
          </FormField>
        </div>
      </Card.Body>
      <Card.Footer>
        <div className="flex flex-wrap justify-end gap-2">
          <Button variant="secondary" onClick={onTest} disabled={isTesting}>
            {isTesting ? 'Sending...' : 'Send Test Message'}
          </Button>
          <Button variant="primary" onClick={handleSave} disabled={isSaving}>
            {isSaving ? 'Saving...' : 'Save Telegram Settings'}
          </Button>
        </div>
      </Card.Footer>
    </Card>
  );
}
