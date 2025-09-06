import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, fireEvent, cleanup } from '@testing-library/react';
import { SpoolsPage } from '../../pages/SpoolsPage';
import { BrowserRouter } from 'react-router-dom';

// Minimal fetch sequence mock
interface MockResp { ok: boolean; status?: number; body?: unknown; }
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

describe('SpoolsPage column config', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    localStorage.clear();
  });

  const baseData = [
    { id: 1, name: 'Spool 1', material: 'PLA', remainingWeightG: 750, colorHex: '#ff0000', inUse: true, filamentName: 'Red PLA', vendor: 'VendorA', initialWeightG: 1000, usedWeightG: 250 },
  ];

  it('allows toggling a column visibility', async () => {
    mockFetchSequence([
      { ok: true, body: { baseUrl: 'http://spoolman.local:7912' } },
      { ok: true, body: baseData },
    ]);

    render(wrapper(<SpoolsPage />));

    await waitFor(() => expect(screen.getByText(/Red PLA/)).toBeTruthy());

    // Switch to table view
    fireEvent.click(screen.getByRole('button', { name: /Table view/i }));

    // Open column config
    fireEvent.click(screen.getByRole('button', { name: /Columns/i }));

  const vendorHeader = screen.getAllByText('Vendor').find(el => el.tagName === 'TH' || el.closest('th'));
  expect(vendorHeader).toBeTruthy();

    // Uncheck vendor column
  const vendorToggle = screen.getAllByLabelText(/Toggle column Vendor/i)[0];
    fireEvent.click(vendorToggle);

    // Vendor header should disappear
  const vendorAfterHide = screen.getAllByText('Vendor').find(el => el.tagName === 'TH' || el.closest('th'));
  expect(vendorAfterHide).toBeUndefined();

    // Re-check vendor column
  fireEvent.click(vendorToggle);
  const vendorAfterShow = screen.getAllByText('Vendor').find(el => el.tagName === 'TH' || el.closest('th'));
  expect(vendorAfterShow).toBeTruthy();
  });

  it('persists visibility in localStorage', async () => {
    mockFetchSequence([
      { ok: true, body: { baseUrl: 'http://spoolman.local:7912' } },
      { ok: true, body: baseData },
    ]);

    render(wrapper(<SpoolsPage />));
    await waitFor(() => expect(screen.getByText(/Red PLA/)).toBeTruthy());
    fireEvent.click(screen.getByRole('button', { name: /Table view/i }));
    fireEvent.click(screen.getByRole('button', { name: /Columns/i }));

  const locationToggle = screen.getAllByLabelText(/Toggle column Location/i)[0];
    fireEvent.click(locationToggle); // hide it
  const locHeaderHidden = screen.getAllByText('Location').find(el => el.tagName === 'TH' || el.closest('th'));
  expect(locHeaderHidden).toBeUndefined();

  // Unmount and re-mount to pick up persisted config
  cleanup();
  render(wrapper(<SpoolsPage />));
    await waitFor(() => expect(screen.getAllByText(/Red PLA/).length).toBeGreaterThan(0));
    fireEvent.click(screen.getByRole('button', { name: /Table view/i }));
    // Location header should still be hidden
  const locHeaderPersist = screen.getAllByText('Location').find(el => el.tagName === 'TH' || el.closest('th'));
  expect(locHeaderPersist).toBeUndefined();
  });

  it('supports drag-and-drop reordering changing header order', async () => {
    mockFetchSequence([
      { ok: true, body: { baseUrl: 'http://spoolman.local:7912' } },
      { ok: true, body: baseData },
    ]);

    render(wrapper(<SpoolsPage />));
    await waitFor(() => expect(screen.getByText(/Red PLA/)).toBeTruthy());
    fireEvent.click(screen.getByRole('button', { name: /Table view/i }));
    fireEvent.click(screen.getByRole('button', { name: /Columns/i }));

    // Simulate drag: move 'Name' column (id name) before 'Vendor'
    const list = screen.getByRole('dialog', { name: /Column configuration/i });
    const nameItem = list.querySelector('[data-col-id="name"]');
    const vendorItem = list.querySelector('[data-col-id="vendor"]');
    expect(nameItem).toBeTruthy();
    expect(vendorItem).toBeTruthy();

    interface DtLike {
      dataStore: Record<string, string>;
      setData: (k: string, v: string) => void;
      getData: (k: string) => string;
      effectAllowed: string;
    }
    const fireDrag = (type: string, target: Element, data?: Record<string, string>) => {
      const event = new Event(type, { bubbles: true, cancelable: true }) as Event & { dataTransfer?: DtLike };
      if (data) {
        const store: Record<string, string> = { ...data };
        event.dataTransfer = {
          dataStore: store,
          setData: (k: string, v: string) => { store[k] = v; },
          getData: (k: string) => store[k],
          effectAllowed: 'move'
        };
      }
      target.dispatchEvent(event);
    };

    // start drag on name
    fireDrag('dragstart', nameItem!, { 'text/plain': 'name' });
    // over vendor
    fireDrag('dragover', vendorItem!);
    // drop on vendor
    fireDrag('drop', vendorItem!, { 'text/plain': 'name' });

    // Close config popover
    fireEvent.click(screen.getByRole('button', { name: /Close column configuration/i }));

    // Inspect header order in table
    const headers = Array.from(screen.getAllByRole('columnheader')).map(h => h.textContent?.trim());
    // Expect Name to appear before Vendor now
    const nameIdx = headers.findIndex(h => h === 'Name');
    const vendorIdx = headers.findIndex(h => h === 'Vendor');
    expect(nameIdx).toBeGreaterThan(-1);
    expect(vendorIdx).toBeGreaterThan(-1);
    expect(nameIdx).toBeLessThan(vendorIdx);
  });
});
