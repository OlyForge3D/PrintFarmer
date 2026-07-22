import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { userEvent } from '@testing-library/user-event';
import { NfcBindingModal } from '../NfcBindingModal';
import type { NfcTagUnknownEvent } from '@/features/nfc/types';

// Mock hooks
vi.mock('@/common/hooks/useApi', () => ({
  usePrinters: () => ({
    data: [
      { id: 'printer-1', name: 'Printer One' },
      { id: 'printer-2', name: 'Printer Two' },
    ],
    isLoading: false,
  }),
  useLinkNfcTag: () => ({
    mutate: vi.fn(),
    isPending: false,
  }),
}));

function makeEvent(tagUid: string, printerId = 'printer-1'): NfcTagUnknownEvent {
  return { tagUid, printerId };
}

describe('NfcBindingModal', () => {
  const onClose = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('resets form fields when event (tag) changes', async () => {
    const eventA = makeEvent('TAG-AAA');
    const { rerender } = render(
      <NfcBindingModal key="TAG-AAA" isOpen={true} onClose={onClose} event={eventA} />,
    );

    // Fill in spool ID
    const user = userEvent.setup();
    const spoolInput = screen.getByLabelText('Spool ID');
    await user.type(spoolInput, '42');
    expect(spoolInput).toHaveValue('42');

    // Select a printer
    const printerSelect = screen.getByLabelText('Printer');
    await user.selectOptions(printerSelect, 'printer-2');
    expect(printerSelect).toHaveValue('printer-2');

    // New scan (tag B) — remount via key change
    const eventB = makeEvent('TAG-BBB', 'printer-2');
    rerender(
      <NfcBindingModal key="TAG-BBB" isOpen={true} onClose={onClose} event={eventB} />,
    );

    // Form should reset: spool empty, printer set to eventB's printerId
    const spoolAfter = screen.getByLabelText('Spool ID');
    expect(spoolAfter).toHaveValue('');

    const printerAfter = screen.getByLabelText('Printer');
    expect(printerAfter).toHaveValue('printer-2');
  });

  it('resets form fields on close and reopen with new event', async () => {
    const eventA = makeEvent('TAG-XXX');
    const { rerender } = render(
      <NfcBindingModal key="TAG-XXX" isOpen={true} onClose={onClose} event={eventA} />,
    );

    const user = userEvent.setup();
    const spoolInput = screen.getByLabelText('Spool ID');
    await user.type(spoolInput, '99');
    expect(spoolInput).toHaveValue('99');

    // Close modal — parent sets event to null and key changes
    rerender(
      <NfcBindingModal key="" isOpen={false} onClose={onClose} event={null} />,
    );

    // Reopen with tag B
    const eventB = makeEvent('TAG-YYY');
    rerender(
      <NfcBindingModal key="TAG-YYY" isOpen={true} onClose={onClose} event={eventB} />,
    );

    const spoolAfter = screen.getByLabelText('Spool ID');
    expect(spoolAfter).toHaveValue('');
  });
});
