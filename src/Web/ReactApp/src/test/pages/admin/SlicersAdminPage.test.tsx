import React from 'react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor, act } from '@testing-library/react';
import type { Mock } from 'vitest';
import SlicersAdminPage from '../../../../src/pages/admin/SlicersAdminPage';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

vi.mock('../../../../src/services/slicerRegistry', () => ({
  slicerRegistry: {
    getSlicers: vi.fn(async () => [
      { id: 's1', name: 'Orca-1', slicerType: 'orcaslicer', version: '1.0', host: 'http://10.0.0.1', status: 'online', lastSeen: new Date().toISOString() }
    ]),
    deregisterSlicer: vi.fn(async () => undefined)
  }
}));

// Mock ProtectedRoute to just render children (bypass auth)
vi.mock('@/components/auth/ProtectedRoute', () => ({
  ProtectedRoute: ({ children }: { children: React.ReactNode }) => <div>{children}</div>
}));

describe('SlicersAdminPage', () => {
  let queryClient: QueryClient;

  beforeEach(() => {
    queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  });

  it('renders list and allows deregister', async () => {
    render(
      <QueryClientProvider client={queryClient}>
        <SlicersAdminPage />
      </QueryClientProvider>
    );

    expect(await screen.findByText('Admin: Slicers')).toBeTruthy();
    expect(await screen.findByText('Orca-1')).toBeTruthy();

    const deregBtn = screen.getByRole('button', { name: /Deregister/i });
    expect(deregBtn).toBeTruthy();

    await act(async () => {
      fireEvent.click(deregBtn);

      type SlicerRegistryMock = { slicerRegistry: { deregisterSlicer: Mock } };
      const mod = (await vi.importMock('../../../../src/services/slicerRegistry')) as unknown as SlicerRegistryMock;
      await waitFor(() => expect(mod.slicerRegistry.deregisterSlicer).toHaveBeenCalled());
    });
  });
});
