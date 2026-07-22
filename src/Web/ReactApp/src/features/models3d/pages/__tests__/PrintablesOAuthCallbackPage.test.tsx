import { render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { MemoryRouter, Route, Routes } from 'react-router';
import { PrintablesOAuthCallbackPage } from '../PrintablesOAuthCallbackPage';

const { completePrintablesOAuthCallback } = vi.hoisted(() => ({
  completePrintablesOAuthCallback: vi.fn(),
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    completePrintablesOAuthCallback,
  },
}));

describe('PrintablesOAuthCallbackPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('completes callback and redirects to settings', async () => {
    completePrintablesOAuthCallback.mockResolvedValue({
      isLinked: true,
      hasRefreshToken: true,
    });

    render(
      <MemoryRouter initialEntries={['/oauth/printables/callback?code=oauth-code&state=oauth-state']}>
        <Routes>
          <Route path="/oauth/printables/callback" element={<PrintablesOAuthCallbackPage />} />
          <Route path="/settings" element={<div>Settings Page</div>} />
        </Routes>
      </MemoryRouter>
    );

    await waitFor(() => {
      expect(completePrintablesOAuthCallback).toHaveBeenCalledWith('oauth-code', 'oauth-state');
    });
    expect(await screen.findByText('Settings Page')).toBeInTheDocument();
  });

  it('shows validation error when query params are missing', async () => {
    render(
      <MemoryRouter initialEntries={['/oauth/printables/callback']}>
        <Routes>
          <Route path="/oauth/printables/callback" element={<PrintablesOAuthCallbackPage />} />
        </Routes>
      </MemoryRouter>
    );

    expect(await screen.findByText('Printables connection failed')).toBeInTheDocument();
    expect(completePrintablesOAuthCallback).not.toHaveBeenCalled();
  });
});
