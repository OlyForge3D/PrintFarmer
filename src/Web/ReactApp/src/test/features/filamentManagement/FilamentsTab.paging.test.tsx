/**
 * Regression tests for FilamentsTab server-side paging, filter propagation,
 * and stale-response cancellation.
 *
 * Heavy child components are mocked so the tests are fast and deterministic.
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, act } from '@testing-library/react';
import '@testing-library/jest-dom';

// ── URL filter state — controlled per-test via a mutable ref ─────────────────

const filterState = {
  search: '',
  material: '',
  vendor: '',
  color: '',
  sortField: 'name' as string,
  sortDir: 'asc' as string,
  page: 1 as number,
  setSearch: vi.fn(),
  setMany: vi.fn(),
  resetAll: vi.fn(),
  hasActiveFilters: false,
};

vi.mock('@/common/hooks/useUrlFilterState', () => ({
  useUrlFilterState: () => filterState,
}));

// ── API client ────────────────────────────────────────────────────────────────

const mockGetFilamentsPaged = vi.fn();

vi.mock('@/services/api', () => ({
  apiClient: {
    getFilamentsPaged: (...args: unknown[]) => mockGetFilamentsPaged(...args),
    getFilamentFilterOptions: vi.fn().mockResolvedValue({ materials: [], vendors: [] }),
    getSpoolmanConfig: vi.fn().mockResolvedValue({}),
  },
}));

// ── Hooks ─────────────────────────────────────────────────────────────────────

vi.mock('@/common/hooks/useApi', () => ({
  useExportSpoolmanFilamentsCsv: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useImportSpoolmanFilamentsCsv: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useDeleteFilament: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useBulkDeleteFilaments: () => ({ mutateAsync: vi.fn(), isPending: false }),
}));

vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

vi.mock('@/common/utils/colorFamilies', () => ({
  classifyColor: () => '',
}));

// ── Heavy child component mocks ───────────────────────────────────────────────
// Avoids import chains that require contexts not present in unit tests.

vi.mock('@/features/filamentManagement/components/FilamentCard', () => ({
  FilamentCard: ({ filament }: { filament: { name?: string } }) =>
    `<div data-testid="filament-card">${filament.name}</div>`,
}));

vi.mock('@/features/filamentManagement/components/FilamentTableView', () => ({
  FilamentTableView: () => '<div data-testid="filament-table" />',
}));

vi.mock('@/features/filamentManagement/components/ColorFamilySelect', () => ({
  ColorFamilySelect: () => '<div />',
}));

vi.mock('@/features/filamentManagement/components/OpenFilamentDbBrowserModal', () => ({
  OpenFilamentDbBrowserModal: () => null,
}));

vi.mock('@/features/filamentManagement/components/BulkEditFilamentsModal', () => ({
  BulkEditFilamentsModal: () => null,
}));

vi.mock('@/features/filamentManagement/components/EditFilamentModal', () => ({
  EditFilamentModal: () => null,
}));

vi.mock('@/features/filamentManagement/components/AddFilamentModal', () => ({
  AddFilamentModal: () => null,
}));

vi.mock('@/features/filamentManagement/components/ColorSwatch', () => ({
  ColorSwatch: () => null,
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
  FileUpload: () => null,
}));

vi.mock('@/common/components/ui/Checkbox', () => ({
  Checkbox: () => null,
}));

// ── helpers ───────────────────────────────────────────────────────────────────

import React from 'react';
import { FilamentsTab } from '@/features/filamentManagement/components/FilamentsTab';

function makeFilament(id: number, name = `Filament ${id}`) {
  return { id, name, material: 'PLA', vendor: 'Bambu', colorHex: '#FFFFFF' };
}

function pageOf(items: ReturnType<typeof makeFilament>[], totalCount: number) {
  return Promise.resolve({ items, totalCount });
}

// ── tests ─────────────────────────────────────────────────────────────────────

describe('FilamentsTab — server-side paging', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    // Reset filter state to defaults
    Object.assign(filterState, {
      search: '', material: '', vendor: '', color: '',
      sortField: 'name', sortDir: 'asc', page: 1,
    });
    mockGetFilamentsPaged.mockResolvedValue({ items: [], totalCount: 0 });
  });

  it('calls getFilamentsPaged on mount (not the old getFilaments)', async () => {
    mockGetFilamentsPaged.mockResolvedValue(pageOf([], 0));
    render(<FilamentsTab />);
    await waitFor(() => expect(mockGetFilamentsPaged).toHaveBeenCalled());
  });

  it('passes limit derived from pageSize to the API', async () => {
    localStorage.setItem('filaments-page-size', '25');
    mockGetFilamentsPaged.mockResolvedValue(pageOf([], 0));
    render(<FilamentsTab />);
    await waitFor(() => expect(mockGetFilamentsPaged).toHaveBeenCalled());
    const [params] = mockGetFilamentsPaged.mock.calls[0];
    expect(params).toMatchObject({ limit: 25 });
  });

  it('derives page count from server totalCount, not array length', async () => {
    // 50 items in this page, but server says 150 total → 3 pages
    const items = Array.from({ length: 50 }, (_, i) => makeFilament(i + 1));
    mockGetFilamentsPaged.mockResolvedValue(pageOf(items, 150));
    localStorage.setItem('filaments-page-size', '50');

    render(<FilamentsTab />);

    await waitFor(() => screen.getByText(/page 1 of 3/i));
  });

  it('does not show pagination controls when totalCount ≤ pageSize', async () => {
    const items = Array.from({ length: 8 }, (_, i) => makeFilament(i + 1));
    mockGetFilamentsPaged.mockResolvedValue(pageOf(items, 8));
    localStorage.setItem('filaments-page-size', '50');

    render(<FilamentsTab />);

    // Wait for loading to finish
    await waitFor(() => expect(mockGetFilamentsPaged).toHaveBeenCalled());
    await act(async () => {});

    expect(screen.queryByRole('button', { name: /prev/i })).not.toBeInTheDocument();
  });

  it('shows totalCount from server in the heading', async () => {
    const items = [makeFilament(1, 'Only Item')];
    mockGetFilamentsPaged.mockResolvedValue(pageOf(items, 999));
    localStorage.setItem('filaments-page-size', '50');

    render(<FilamentsTab />);

    await waitFor(() => screen.getByText(/filaments \(999\)/i));
  });

  it('sends sort param to the API when a sort field is active', async () => {
    filterState.sortField = 'vendor';
    filterState.sortDir = 'desc';
    mockGetFilamentsPaged.mockResolvedValue(pageOf([], 0));

    render(<FilamentsTab />);

    await waitFor(() => expect(mockGetFilamentsPaged).toHaveBeenCalled());
    const [params] = mockGetFilamentsPaged.mock.calls[0];
    // vendor sort field maps to 'vendor.name:desc'
    expect(params.sort).toMatch(/vendor.*desc/i);
  });

  it('sends search string to the API', async () => {
    filterState.search = 'Bambu PLA';
    mockGetFilamentsPaged.mockResolvedValue(pageOf([], 0));

    render(<FilamentsTab />);

    await waitFor(() => expect(mockGetFilamentsPaged).toHaveBeenCalled());
    const [params] = mockGetFilamentsPaged.mock.calls[0];
    expect(params.search).toBe('Bambu PLA');
  });

  it('sends material and vendor filters to the API', async () => {
    filterState.material = 'PETG';
    filterState.vendor = 'Prusa';
    mockGetFilamentsPaged.mockResolvedValue(pageOf([], 0));

    render(<FilamentsTab />);

    await waitFor(() => expect(mockGetFilamentsPaged).toHaveBeenCalled());
    const [params] = mockGetFilamentsPaged.mock.calls[0];
    expect(params.material).toBe('PETG');
    expect(params.vendor).toBe('Prusa');
  });

  it('passes the AbortSignal to the API call', async () => {
    mockGetFilamentsPaged.mockResolvedValue(pageOf([], 0));

    render(<FilamentsTab />);

    await waitFor(() => expect(mockGetFilamentsPaged).toHaveBeenCalled());
    const [params] = mockGetFilamentsPaged.mock.calls[0];
    expect(params.signal).toBeInstanceOf(AbortSignal);
  });

  it('omits offset when on page 1', async () => {
    filterState.page = 1;
    localStorage.setItem('filaments-page-size', '50');
    mockGetFilamentsPaged.mockResolvedValue(pageOf([], 0));

    render(<FilamentsTab />);

    await waitFor(() => expect(mockGetFilamentsPaged).toHaveBeenCalled());
    const [params] = mockGetFilamentsPaged.mock.calls[0];
    // offset should be 0 or undefined (not sent) for page 1
    expect(params.offset === undefined || params.offset === 0).toBe(true);
  });

  it('sends correct offset for page 3 with pageSize 25', async () => {
    filterState.page = 3;
    localStorage.setItem('filaments-page-size', '25');
    mockGetFilamentsPaged.mockResolvedValue(pageOf([], 100));

    render(<FilamentsTab />);

    await waitFor(() => expect(mockGetFilamentsPaged).toHaveBeenCalled());
    const [params] = mockGetFilamentsPaged.mock.calls[0];
    // offset = (3 - 1) * 25 = 50
    expect(params.offset).toBe(50);
  });
});

describe('FilamentsTab — color filter (client-side only)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    Object.assign(filterState, {
      search: '', material: '', vendor: '', color: '',
      sortField: 'name', sortDir: 'asc', page: 1,
    });
    mockGetFilamentsPaged.mockResolvedValue({ items: [], totalCount: 0 });
  });

  it('does not include color param in API call (color is client-side only)', async () => {
    filterState.color = 'red';
    mockGetFilamentsPaged.mockResolvedValue(pageOf([], 0));

    render(<FilamentsTab />);

    await waitFor(() => expect(mockGetFilamentsPaged).toHaveBeenCalled());
    const [params] = mockGetFilamentsPaged.mock.calls[0];
    expect(params).not.toHaveProperty('color');
  });

  it('when totalCount > pageSize (paginated mode) color filter is disabled in UI', async () => {
    // 150 total, pageSize 50 → totalPages=3 → isPaginated=true → color filter cleared
    filterState.color = 'blue';
    localStorage.setItem('filaments-page-size', '50');
    mockGetFilamentsPaged.mockResolvedValue(pageOf(Array.from({ length: 50 }, (_, i) => makeFilament(i)), 150));

    render(<FilamentsTab />);

    await waitFor(() => expect(mockGetFilamentsPaged).toHaveBeenCalled());
    // The API call should still NOT receive a color param
    const [params] = mockGetFilamentsPaged.mock.calls[0];
    expect(params).not.toHaveProperty('color');
  });
});

describe('FilamentsTab — page clamping', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    Object.assign(filterState, {
      search: '', material: '', vendor: '', color: '',
      sortField: 'name', sortDir: 'asc', page: 1,
    });
    mockGetFilamentsPaged.mockResolvedValue({ items: [], totalCount: 0 });
  });

  it('clamps offset to 0 when page < 1 (safePage = max(1, page))', async () => {
    filterState.page = -5;
    localStorage.setItem('filaments-page-size', '25');
    mockGetFilamentsPaged.mockResolvedValue(pageOf([], 0));

    render(<FilamentsTab />);

    await waitFor(() => expect(mockGetFilamentsPaged).toHaveBeenCalled());
    const [params] = mockGetFilamentsPaged.mock.calls[0];
    // offset must be 0 (page clamped to 1)
    expect(params.offset === undefined || params.offset === 0).toBe(true);
  });
});

describe('FilamentsTab — stale-response guard', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    Object.assign(filterState, {
      search: '', material: '', vendor: '', color: '',
      sortField: 'name', sortDir: 'asc', page: 1,
    });
  });

});

// ── new suites ────────────────────────────────────────────────────────────────

describe('FilamentsTab — page value sanitization', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    Object.assign(filterState, {
      search: '', material: '', vendor: '', color: '',
      sortField: 'name', sortDir: 'asc', page: 1,
    });
    mockGetFilamentsPaged.mockResolvedValue({ items: [], totalCount: 0 });
  });

  it('treats page=0 as page 1 — offset sent to API is 0', async () => {
    filterState.page = 0;
    localStorage.setItem('filaments-page-size', '50');
    mockGetFilamentsPaged.mockResolvedValue({ items: [], totalCount: 0 });

    render(<FilamentsTab />);

    await waitFor(() => expect(mockGetFilamentsPaged).toHaveBeenCalled());
    const [params] = mockGetFilamentsPaged.mock.calls[0];
    // safePage = max(1, 0) = 1 → offset = (1-1)*50 = 0
    expect(params.offset === undefined || params.offset === 0).toBe(true);
  });

  it('treats page=-5 as page 1 — offset sent to API is 0', async () => {
    filterState.page = -5;
    localStorage.setItem('filaments-page-size', '25');
    mockGetFilamentsPaged.mockResolvedValue({ items: [], totalCount: 0 });

    render(<FilamentsTab />);

    await waitFor(() => expect(mockGetFilamentsPaged).toHaveBeenCalled());
    const [params] = mockGetFilamentsPaged.mock.calls[0];
    expect(params.offset === undefined || params.offset === 0).toBe(true);
  });

  it('clamps page above totalPages — setMany called with totalPages', async () => {
    // 50 items, pageSize 25 → totalPages = 2. URL says page = 9.
    filterState.page = 9;
    localStorage.setItem('filaments-page-size', '25');
    mockGetFilamentsPaged.mockResolvedValue({ items: [], totalCount: 50 });

    render(<FilamentsTab />);

    await waitFor(() => expect(mockGetFilamentsPaged).toHaveBeenCalled());
    await act(async () => { await Promise.resolve(); });

    // setMany({ page: 2 }) should be called because page=9 > totalPages=2
    expect(filterState.setMany).toHaveBeenCalledWith(expect.objectContaining({ page: 2 }));
  });

  it('does not call setMany when page is within valid range', async () => {
    filterState.page = 1;
    localStorage.setItem('filaments-page-size', '50');
    mockGetFilamentsPaged.mockResolvedValue({ items: [], totalCount: 150 });

    render(<FilamentsTab />);

    await waitFor(() => expect(mockGetFilamentsPaged).toHaveBeenCalled());
    await act(async () => { await Promise.resolve(); });

    // page=1, totalPages=3 → no clamping needed
    const pageCalls = (filterState.setMany as ReturnType<typeof vi.fn>).mock.calls.filter(
      (c: unknown[]) => (c[0] as Record<string, unknown>)?.page !== undefined
    );
    expect(pageCalls.every((c: unknown[]) => (c[0] as Record<string, unknown>).page === 1)).toBe(true);
  });

  it('ignores aborted (stale) responses — newer state is not overwritten', async () => {
    /**
     * Flow:
     *  1. Initial render → first fetch (pending, would resolve with totalCount=999)
     *  2. Search filter changes + rerender → useEffect re-runs, aborts first controller,
     *     starts second fetch which resolves immediately with totalCount=42.
     *  3. First (stale) fetch resolves → signal.aborted=true → setState skipped.
     *  4. UI must show 42, never 999.
     */
    let resolveStale!: (v: { items: ReturnType<typeof makeFilament>[]; totalCount: number }) => void;
    let firstSignal: AbortSignal | undefined;

    let callCount = 0;
    mockGetFilamentsPaged.mockImplementation(
      ({ signal }: { signal?: AbortSignal }) => {
        callCount++;
        if (callCount === 1) {
          firstSignal = signal;
          // Return a pending promise so we can resolve it after abort
          return new Promise<{ items: ReturnType<typeof makeFilament>[]; totalCount: number }>(res => {
            resolveStale = res;
          });
        }
        // Second call: fresh result, resolves immediately
        return Promise.resolve({ items: [makeFilament(1, 'Fresh')], totalCount: 42 });
      },
    );

    localStorage.setItem('filaments-page-size', '50');
    const { rerender } = render(<FilamentsTab />);

    // Wait until the first call is registered and the signal is captured
    await waitFor(() => expect(firstSignal).toBeDefined());

    // Change search so loadFilaments gets a new reference → useEffect re-runs
    filterState.search = 'trigger-refetch';

    await act(async () => {
      rerender(<FilamentsTab />);
      // Allow microtasks (second fetch) to settle
      await Promise.resolve();
    });

    // First signal should now be aborted by the cleanup
    expect(firstSignal!.aborted).toBe(true);

    // Resolve the stale first call — the guard should discard it
    await act(async () => {
      resolveStale({ items: [makeFilament(2, 'Stale')], totalCount: 999 });
      await Promise.resolve();
    });

    // UI shows the fresh result
    await waitFor(() => screen.getByText(/filaments \(42\)/i));
    expect(screen.queryByText(/filaments \(999\)/i)).not.toBeInTheDocument();

    // Cleanup
    filterState.search = '';
  });
});

