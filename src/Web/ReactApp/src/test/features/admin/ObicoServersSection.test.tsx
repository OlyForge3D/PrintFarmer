import '@testing-library/jest-dom';
import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ObicoServersSection } from '@/features/admin/components/ObicoServersSection';
import type { ObicoServer } from '@/types/api';

const server: ObicoServer = {
  id: 'pool',
  name: 'GPU pool',
  url: 'http://pool.invalid',
  isEnabled: true,
  hasApiKey: false,
  maxConcurrentAnalyses: 4,
  activePrintCount: 2,
  createdAt: '2026-11-06T00:00:00Z',
  updatedAt: '2026-11-06T00:00:00Z',
};

vi.mock('@/common/hooks/useApi', () => ({
  useObicoServers: () => ({ data: [server], isLoading: false }),
  useCreateObicoServer: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useUpdateObicoServer: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useDeleteObicoServer: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useTestObicoServerHealth: () => ({ mutateAsync: vi.fn(), isPending: false }),
}));

describe('ObicoServersSection', () => {
  it('describes print-lifetime capacity without promising global overflow', () => {
    render(<ObicoServersSection />);

    expect(screen.getByText('Active print reservations: 2 / 4')).toBeInTheDocument();
    expect(screen.getByText(/Each print reserves an available, healthy server until it ends/)).toHaveTextContent(
      'The global URL is used only when no pool is registered, never as overflow',
    );
    expect(screen.getByText(/Monitoring never pauses printing/)).toBeInTheDocument();
  });

  it('explains why active reservations prevent deleting a server', () => {
    render(<ObicoServersSection />);
    fireEvent.click(screen.getByRole('button', { name: 'Delete' }));

    expect(screen.getByText(/Servers with active print reservations cannot be deleted/)).toHaveTextContent(
      'existing prints continue without Obico monitoring until it is re-enabled',
    );
    expect(screen.queryByText(/will fall back to the default configuration/)).not.toBeInTheDocument();
  });
});
