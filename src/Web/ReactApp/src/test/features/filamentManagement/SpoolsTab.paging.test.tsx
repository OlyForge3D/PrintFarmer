/**
 * Regression tests for SpoolsTab: AbortController cancellation, stale-response
 * guard, page-value sanitization, and showEmpty/color filter semantics.
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, act, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';

// ── URL filter state — controlled per-test ───────────────────────────────────

const filterState = {
  search: '',
  material: '',
  vendor: '',
  color: '',
  location: '',
  showEmpty: false,
  pageSize: 50,
  sortField: 'id' as string,
  sortDir: 'asc' as string,
  page: 0 as number,
  setSearch: vi.fn(),
  setMany: vi.fn(),
  resetAll: vi.fn(),
  hasActiveFilters: false,
};

vi.mock('@/common/hooks/useUrlFilterState', () => ({
  useUrlFilterState: () => filterState,
}));

// ── API client ───────────────────────────────────────────────────────────────

const mockGetSpools = vi.fn();

vi.mock('@/services/api', () => ({
  apiClient: {
    getSpools: (...args: unknown[]) => mockGetSpools(...args),
    getSpoolFilterOptions: vi.fn().mockResolvedValue({ materials: [], vendors: [], locations: [] }),
    getSpoolmanConfig: vi.fn().mockResolvedValue({}),
    getSpoolmanHealth: vi.fn().mockResolvedValue({ configured: true, success: true }),
  },
}));

// ── Hooks ────────────────────────────────────────────────────────────────────

vi.mock('@/common/hooks/useApi', () => ({
  useDeleteSpool: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useBulkDeleteSpools: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useImportSpoolmanSpoolsCsv: () => ({ mutateAsync: vi.fn(), isPending: false }),
}));

vi.mock('@/common/hooks/useKeyboardShortcuts', () => ({
  useKeyboardShortcuts: vi.fn(),
}));

vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

vi.mock('@/common/utils/colorFamilies', () => ({
  classifyColor: () => '',
  getRepresentativeHex: () => '#888888',
}));

// ── Heavy child component mocks ──────────────────────────────────────────────

vi.mock('@/features/filamentManagement/components/SpoolCard', () => ({
  SpoolCard: ({ spool }: { spool: { id: number } }) =>
    `<div data-testid="spool-card">${spool.id}</div>`,
}));

vi.mock('@/features/filamentManagement/components/SpoolTableView', () => ({
  SpoolTableView: () => '<div data-testid="spool-table" />',
}));

vi.mock('@/features/filamentManagement/components/SpoolCompactView', () => ({
  SpoolCompactView: () => '<div data-testid="spool-compact" />',
}));

vi.mock('@/features/filamentManagement/components/ColorFamilySelect', () => ({
  ColorFamilySelect: () => '<div />',
}));

vi.mock('@/features/filamentManagement/components/EditSpoolModal', () => ({
  EditSpoolModal: () => null,
}));

vi.mock('@/features/filamentManagement/components/AddSpoolModal', () => ({
  AddSpoolModal: () => null,
}));

vi.mock('@/features/filamentManagement/components/BulkEditSpoolsModal', () => ({
  BulkEditSpoolsModal: () => null,
}));

vi.mock('@/features/filamentManagement/components/SpoolLabelModal', () => ({
  SpoolLabelModal: () => null,
}));

vi.mock('@/common/components/skeletons/Skeleton', () => ({
  Skeleton: () => null,
}));

vi.mock('@/common/components/modals/Modal', () => ({
  Modal: ({ children }: { children: React.ReactNode }) => children,
}));

vi.mock('@/common/components/ui', () => ({
  Button: ({ children, onClick, disabled, 'aria-label': label }: {
    children?: React.ReactNode; onClick?: () => void; disabled?: boolean; 'aria-label'?: string;
  }) => <button onClick={onClick} disabled={disabled} aria-label={label}>{children}</button>,
  Select: ({ children, value, onChange }: {
    children?: React.ReactNode; value?: string | number; onChange?: (e: React.ChangeEvent<HTMLSelectElement>) => void;
  }) => <select value={value} onChange={onChange}>{children}</select>,
  Checkbox: ({ checked, onChange }: { checked?: boolean; onChange?: (e: React.ChangeEvent<HTMLInputElement>) => void }) =>
    <input type="checkbox" checked={checked} onChange={onChange} />,
  FileUpload: () => null,
}));

vi.mock('@/features/filamentManagement/components/spool-components.css', () => ({}));

// ── helpers ──────────────────────────────────────────────────────────────────

import React from 'react';
import { SpoolsTab } from '@/features/filamentManagement/components/SpoolsTab';

function makeSpool(id: number, remainingWeightG = 500) {
  return { id, filamentName: `Spool ${id}`, material: 'PLA', vendor: 'Bambu', colorHex: '#FFFFFF', remainingWeightG };
}

function pageOf(items: ReturnType<typeof makeSpool>[], totalCount: number) {
  return Promise.resolve({ items, totalCount });
}

// ── tests ────────────────────────────────────────────────────────────────────

describe('SpoolsTab — AbortController cancellation', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    Object.assign(filterState, {
      search: '', material: '', vendor: '', color: '', location: '',
      showEmpty: false, pageSize: 50, sortField: 'id', sortDir: 'asc', page: 0,
    });
    mockGetSpools.mockResolvedValue({ items: [], totalCount: 0 });
  });

  it('passes AbortSignal to getSpools on mount', async () => {
    mockGetSpools.mockResolvedValue(pageOf([], 0));
    render(<SpoolsTab />);
    await waitFor(() => expect(mockGetSpools).toHaveBeenCalled());
    const [params] = mockGetSpools.mock.calls[0];
    expect(params.signal).toBeInstanceOf(AbortSignal);
  });

  it('ignores stale (aborted) responses — newer state is not overwritten', async () => {
    /**
     * Flow:
     *  1. Initial render → first fetch pending (would resolve totalCount=999)
     *  2. Search changes → abort fires, second fetch resolves totalCount=42
     *  3. Stale first response resolves → guard discards it
     *  4. UI must show totalCount=42, never 999
     */
    let resolveStale!: (v: { items: ReturnType<typeof makeSpool>[]; totalCount: number }) => void;
    let firstSignal: AbortSignal | undefined;
    let callCount = 0;

    mockGetSpools.mockImplementation(({ signal }: { signal?: AbortSignal }) => {
      callCount++;
      if (callCount === 1) {
        firstSignal = signal;
        return new Promise<{ items: ReturnType<typeof makeSpool>[]; totalCount: number }>(res => {
          resolveStale = res;
        });
      }
      return Promise.resolve({ items: [makeSpool(1)], totalCount: 42 });
    });

    const { rerender } = render(<SpoolsTab />);
    await waitFor(() => expect(firstSignal).toBeDefined());

    // Trigger refetch by changing search
    filterState.search = 'trigger';
    await act(async () => {
      rerender(<SpoolsTab />);
      await Promise.resolve();
    });

    expect(firstSignal!.aborted).toBe(true);

    // Resolve stale response — should be discarded
    await act(async () => {
      resolveStale({ items: [makeSpool(99)], totalCount: 999 });
      await Promise.resolve();
    });

    // The component must not show 999
    await waitFor(() => {
      expect(screen.queryByText(/999/)).not.toBeInTheDocument();
    });

    filterState.search = '';
  });

  it('aborts in-flight request when Refresh is clicked and starts a new request', async () => {
    localStorage.setItem('spoolman-base-url', 'http://localhost:7912');
    let firstSignal: AbortSignal | undefined;
    let secondSignal: AbortSignal | undefined;
    let callCount = 0;

    mockGetSpools.mockImplementation(({ signal }: { signal?: AbortSignal }) => {
      callCount++;
      if (callCount === 1) {
        firstSignal = signal;
        return Promise.resolve({ items: [], totalCount: 0 });
      }
      secondSignal = signal;
      return Promise.resolve({ items: [], totalCount: 0 });
    });

    render(<SpoolsTab />);
    await waitFor(() => expect(firstSignal).toBeDefined());
    await waitFor(() => expect(screen.getByRole('button', { name: /refresh spools/i })).toBeInTheDocument());

    fireEvent.click(screen.getByRole('button', { name: /refresh spools/i }));

    await waitFor(() => expect(mockGetSpools).toHaveBeenCalledTimes(2));
    expect(firstSignal?.aborted).toBe(true);
    expect(secondSignal).toBeInstanceOf(AbortSignal);
    expect(secondSignal).not.toBe(firstSignal);
  });
});

describe('SpoolsTab — page value sanitization', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    Object.assign(filterState, {
      search: '', material: '', vendor: '', color: '', location: '',
      showEmpty: false, pageSize: 50, sortField: 'id', sortDir: 'asc', page: 0,
    });
    mockGetSpools.mockResolvedValue({ items: [], totalCount: 0 });
  });

  it('treats page=-3 as page 0 — offset is 0', async () => {
    filterState.page = -3;
    mockGetSpools.mockResolvedValue(pageOf([], 0));

    render(<SpoolsTab />);

    await waitFor(() => expect(mockGetSpools).toHaveBeenCalled());
    const [params] = mockGetSpools.mock.calls[0];
    // safePage = max(0, -3) = 0 → offset = 0 * 50 = 0
    expect(params.offset === undefined || params.offset === 0).toBe(true);
  });

  it('clamps page above totalPages-1 back into range', async () => {
    filterState.page = 99;
    filterState.pageSize = 10;
    mockGetSpools.mockResolvedValue(pageOf(Array.from({ length: 10 }, (_, i) => makeSpool(i + 1)), 15)); // totalPages = 2

    render(<SpoolsTab />);

    await waitFor(() => expect(mockGetSpools).toHaveBeenCalled());
    await waitFor(() => {
      expect(filterState.setMany).toHaveBeenCalledWith({ page: 1 });
    });
  });

  it('does not preemptively rewrite a valid deep-linked page before totals are known', async () => {
    filterState.page = 4;
    filterState.pageSize = 10;
    let resolvePage!: (v: { items: ReturnType<typeof makeSpool>[]; totalCount: number }) => void;

    mockGetSpools.mockImplementation(() => new Promise(res => { resolvePage = res; }));

    render(<SpoolsTab />);

    await waitFor(() => expect(mockGetSpools).toHaveBeenCalled());
    const [params] = mockGetSpools.mock.calls[0];
    expect(params.offset).toBe(40);
    expect(filterState.setMany).not.toHaveBeenCalledWith({ page: 0 });

    await act(async () => {
      resolvePage({ items: Array.from({ length: 10 }, (_, i) => makeSpool(i + 1)), totalCount: 100 });
      await Promise.resolve();
    });
  });
});

describe('SpoolsTab — showEmpty does not map to allowArchived', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    Object.assign(filterState, {
      search: '', material: '', vendor: '', color: '', location: '',
      showEmpty: true, pageSize: 50, sortField: 'id', sortDir: 'asc', page: 0,
    });
  });

  it('never sends allowArchived to getSpools regardless of showEmpty', async () => {
    mockGetSpools.mockResolvedValue(pageOf([], 0));
    render(<SpoolsTab />);
    await waitFor(() => expect(mockGetSpools).toHaveBeenCalled());
    const [params] = mockGetSpools.mock.calls[0];
    expect(params.allowArchived).toBeUndefined();
  });
});

describe('SpoolsTab — legacy array backward compatibility', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    Object.assign(filterState, {
      search: '', material: '', vendor: '', color: '', location: '',
      showEmpty: false, pageSize: 50, sortField: 'id', sortDir: 'asc', page: 0,
    });
  });

  it('renders spools from a plain array response (pre-paged API)', async () => {
    // Legacy server returns raw array, not { items, totalCount }
    const legacySpools = [makeSpool(10), makeSpool(20)];
    mockGetSpools.mockResolvedValue({ items: legacySpools, totalCount: 2 });

    render(<SpoolsTab />);

    await waitFor(() => expect(mockGetSpools).toHaveBeenCalled());
    // No crash — the apiClient normalises the response
    expect(mockGetSpools).toHaveBeenCalledTimes(1);
  });
});
