import '@testing-library/jest-dom';
import type { ReactNode } from 'react';
import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { NfcSettingsPage } from '@/features/nfc/pages/NfcSettingsPage';

vi.mock('@/common/components/PageTemplate', () => ({
  PageTemplate: ({
    children,
    embedded,
    title,
  }: {
    children: ReactNode;
    embedded?: boolean;
    title: string;
  }) => (
    <main data-embedded={String(embedded)} data-title={title}>
      {children}
    </main>
  ),
}));
vi.mock('@/features/nfc/pages/NfcDevicesPage', () => ({
  NfcDevicesPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="nfc-readers" data-embedded={String(embedded)}>
      <button type="button">Reader controls</button>
    </div>
  ),
}));
vi.mock('@/features/nfc/pages/NfcBindingsPage', () => ({
  NfcBindingsPage: ({ embedded }: { embedded?: boolean }) => (
    <div data-testid="nfc-bindings" data-embedded={String(embedded)}>
      <button type="button">Binding controls</button>
    </div>
  ),
}));

describe('NFC settings page', () => {
  it('combines readers and bindings under distinct headings while preserving each embedded page', () => {
    render(<NfcSettingsPage embedded />);

    expect(screen.getByRole('main')).toHaveAttribute('data-embedded', 'true');
    expect(screen.getByRole('main')).toHaveAttribute('data-title', 'NFC Settings');
    expect(screen.getByRole('heading', { name: 'NFC Readers', level: 2 })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'NFC Tag Bindings', level: 2 })).toBeInTheDocument();
    expect(screen.getByTestId('nfc-readers')).toHaveAttribute('data-embedded', 'true');
    expect(screen.getByTestId('nfc-bindings')).toHaveAttribute('data-embedded', 'true');
    expect(screen.getByRole('button', { name: 'Reader controls' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Binding controls' })).toBeInTheDocument();
  });

  it('renders standalone with the page frame enabled by default', () => {
    render(<NfcSettingsPage />);

    expect(screen.getByRole('main')).toHaveAttribute('data-embedded', 'false');
    expect(screen.getByRole('main')).toHaveAttribute('data-title', 'NFC Settings');
  });
});
