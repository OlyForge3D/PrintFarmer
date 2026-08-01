import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { PageTemplate } from '@/common/components/PageTemplate';
import { resetPageHeaderGuard } from '@/common/components/pageHeaderGuard';
import { WebhooksAdminPage } from '@/features/webhooks/pages/WebhooksAdminPage';
import { NfcDevicesPage } from '@/features/nfc/pages/NfcDevicesPage';

vi.mock('@/features/webhooks/hooks/useWebhooks', () => ({
  useWebhooks: () => ({ data: [], isLoading: false, error: null }),
  useWebhookEventTypes: () => ({ data: [] }),
  useWebhookDeliveries: () => ({ data: [] }),
  useCreateWebhook: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useUpdateWebhook: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useDeleteWebhook: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useTestWebhook: () => ({ mutateAsync: vi.fn(), isPending: false }),
}));

vi.mock('@/common/hooks/useApi', () => ({
  useNfcDevices: () => ({ data: [], isLoading: false, error: null }),
  useDeleteNfcDevice: () => ({ mutate: vi.fn(), isPending: false }),
}));

/** How SettingsShell mounts a sub-page: its own header, then the page inside it. */
function renderInShell(page: React.ReactNode) {
  return render(
    <PageTemplate title="Admin Console" subtitle="Manage PrintFarmer settings and administration.">
      <h2>Integrations</h2>
      {page}
    </PageTemplate>,
  );
}

describe('admin sub-pages inside the settings shell', () => {
  beforeEach(() => {
    resetPageHeaderGuard();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    resetPageHeaderGuard();
  });

  it('leaves the shell as the only level-1 heading', () => {
    renderInShell(<WebhooksAdminPage embedded />);

    // Before this contract the document read: h1 Admin Console, h2 Integrations,
    // h1 Webhooks — three stacked titles and two h1 elements.
    const headings = screen.getAllByRole('heading', { level: 1 });
    expect(headings).toHaveLength(1);
    expect(headings[0]).toHaveTextContent('Admin Console');
  });

  it('keeps every control it has when standalone', () => {
    const standalone = render(<WebhooksAdminPage />);
    const standaloneControls = screen
      .getAllByRole('button')
      .map((button) => button.textContent?.trim());
    standalone.unmount();
    resetPageHeaderGuard();

    renderInShell(<WebhooksAdminPage embedded />);
    const embeddedControls = screen
      .getAllByRole('button')
      .map((button) => button.textContent?.trim());

    // Embedding removes chrome, never capability. `Add Webhook` lives only in the
    // header's `actions` slot, so dropping the header would have deleted it.
    expect(embeddedControls).toEqual(standaloneControls);
    expect(embeddedControls).toContain('Add Webhook');
  });

  it('drops the page subtitle, which the shell heading already answers', () => {
    renderInShell(<NfcDevicesPage embedded />);

    expect(screen.queryByText('0 registered readers')).not.toBeInTheDocument();
  });

  it('renders its own full header when mounted standalone', () => {
    render(<WebhooksAdminPage />);

    const headings = screen.getAllByRole('heading', { level: 1 });
    expect(headings).toHaveLength(1);
    expect(headings[0]).toHaveTextContent('Webhooks');
  });

  it('does not warn about duplicate headers on a correctly embedded page', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});

    renderInShell(<NfcDevicesPage embedded />);

    expect(warn).not.toHaveBeenCalled();
  });

  it('warns when a page is mounted in the shell without embedded', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});

    renderInShell(<NfcDevicesPage />);

    expect(warn).toHaveBeenCalledTimes(1);
    expect(String(warn.mock.calls[0][0])).toContain('"NFC Devices"');
  });
});
