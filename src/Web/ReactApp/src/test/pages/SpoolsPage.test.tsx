import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { SpoolsPage } from '@/pages/SpoolsPage';
import { BrowserRouter } from 'react-router-dom';

// Simple fetch mock helper
interface MockResp {
  ok: boolean;
  status?: number;
  body?: unknown;
}

function mockFetchSequence(responses: MockResp[]) {
  let call = 0;
  global.fetch = vi.fn().mockImplementation(() => {
    const r = responses[Math.min(call, responses.length - 1)];
    call++;
    const responseLike: Partial<Response> = {
      ok: r.ok,
      status: r.status ?? (r.ok ? 200 : 500),
      json: async () => r.body,
    };
    return Promise.resolve(responseLike as Response);
  });
}

function wrapper(children: React.ReactNode) {
  return <BrowserRouter>{children}</BrowserRouter>;
}

describe('SpoolsPage', () => {
  beforeEach(() => {
    vi.resetAllMocks();
  });
  it('shows error when backend returns 503', async () => {
    mockFetchSequence([
      { ok: true, body: { baseUrl: 'http://spoolman.local:7912' } }, // config
      { ok: false, status: 503 }, // spools
    ]);

    render(wrapper(<SpoolsPage />));

    await waitFor(() => {
      expect(screen.getByText(/Spoolman not configured/i)).toBeTruthy();
    });
  });

  it('renders empty state when no spools', async () => {
    mockFetchSequence([
      { ok: true, body: { baseUrl: 'http://spoolman.local:7912' } },
      { ok: true, body: [] },
    ]);

    render(wrapper(<SpoolsPage />));

    await waitFor(() => {
      expect(screen.getByText(/No spools found/i)).toBeTruthy();
    });
  });

  it('renders spools when data returned', async () => {
    // Provide objects already in the shape returned by backend controller (camelCase)
    mockFetchSequence([
      { ok: true, body: { baseUrl: 'http://spoolman.local:7912' } },
      { ok: true, body: [
        { id: 1, name: 'Spool 1', material: 'PLA', remainingWeightG: 750, colorHex: '#ff0000', inUse: true, filamentName: 'Red PLA', vendor: 'VendorA', initialWeightG: 1000, usedWeightG: 250 },
        { id: 2, name: 'Spool 2', material: 'PETG', remainingWeightG: 100, colorHex: '#00ff00', inUse: false, filamentName: 'Green PETG', vendor: 'VendorB', initialWeightG: 1000, usedWeightG: 900 },
      ] },
    ]);

    render(wrapper(<SpoolsPage />));

    await waitFor(() => {
      const vendorAs = screen.getAllByText(/VendorA/i);
      const vendorBs = screen.getAllByText(/VendorB/i);
      expect(vendorAs.length).toBeGreaterThan(0);
      expect(vendorBs.length).toBeGreaterThan(0);
  expect(screen.getByText(/Red PLA/i)).toBeTruthy();
  expect(screen.getByText(/Green PETG/i)).toBeTruthy();
  // Usage percentages roughly 25% and 90%
  const pct25 = screen.getAllByText(/25\.0% used/);
  const pct90 = screen.getAllByText(/90\.0% used/);
  expect(pct25.length).toBeGreaterThan(0);
  expect(pct90.length).toBeGreaterThan(0);
    });
  });

  it('hides empty spools by default and shows when toggled', async () => {
    mockFetchSequence([
      { ok: true, body: { baseUrl: 'http://spoolman.local:7912' } },
      { ok: true, body: [
        { id: 1, name: 'Full Spool', material: 'PLA', remainingWeightG: 100, colorHex: '#ff0000', inUse: true, filamentName: 'Red PLA', vendor: 'VendorA', initialWeightG: 1000, usedWeightG: 900 },
        { id: 2, name: 'Empty Spool', material: 'PLA', remainingWeightG: 0, colorHex: '#00ff00', inUse: false, filamentName: 'Green PLA', vendor: 'VendorB', initialWeightG: 750, usedWeightG: 750 }
      ] }
    ]);
    render(wrapper(<SpoolsPage />));
    // Wait for list
    await waitFor(() => {
      expect(screen.getByText(/Red PLA/)).toBeTruthy();
    });
    // Empty spool hidden by default
    expect(screen.queryByText(/Green PLA/)).toBeNull();
    // Toggle show empty
    const showEmpty = screen.getByLabelText(/Show empty spools/i);
    showEmpty.click();
    expect(await screen.findByText(/Green PLA/)).toBeTruthy();
  });
});
