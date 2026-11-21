import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { NfcPairingModal } from '@/features/nfc/components/NfcPairingModal';
import type { NfcPairingSession } from '@/features/nfc/hooks/useNfcPairingSession';
import type { NfcTagUnknownEvent } from '@/features/nfc/types';
import { getSpools, linkNfcTag } from '@/services/api/nfcApi';
import type { SpoolmanSpool } from '@/types/api';

vi.mock('@/services/api/nfcApi', () => ({
  getSpools: vi.fn(),
  linkNfcTag: vi.fn(),
}));

const spool: SpoolmanSpool = {
  id: 42,
  name: 'PLA spool',
  material: 'PLA',
  inUse: false,
};

function renderModal(session: NfcPairingSession) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false },
    },
  });

  return render(
    <QueryClientProvider client={queryClient}>
      <NfcPairingModal session={session} />
    </QueryClientProvider>,
  );
}

describe('NfcPairingModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(getSpools).mockResolvedValue({ items: [spool], totalCount: 1 });
    vi.mocked(linkNfcTag).mockResolvedValue({
      id: 'binding-1',
      tagUid: 'AABBCCDD',
      createdAt: '2026-03-01T10:00:00Z',
    });
  });

  it('forwards the scanned readAt timestamp when linking a spool', async () => {
    const user = userEvent.setup();
    const event: NfcTagUnknownEvent = {
      tagUid: 'AABBCCDD',
      printerId: 'printer-1',
      readAt: '2026-03-01T09:30:00Z',
    };
    const session: NfcPairingSession = {
      isOpen: true,
      tagEvent: event,
      isConnected: true,
      isUnavailable: false,
      startScanning: vi.fn(),
      close: vi.fn(),
    };

    renderModal(session);

    await screen.findByPlaceholderText(
      'Search by name, vendor, or material...',
      {},
      { timeout: 3_000 },
    );
    await user.click(await screen.findByRole('button', { name: /PLA spool/ }));
    await user.click(screen.getByRole('button', { name: 'Link' }));

    await waitFor(() => {
      expect(linkNfcTag).toHaveBeenCalledOnce();
      expect(vi.mocked(linkNfcTag).mock.calls[0]?.[0]).toEqual({
        tagUid: event.tagUid,
        spoolId: spool.id,
        readAt: event.readAt,
        printerId: event.printerId,
      });
    });
  });
});
