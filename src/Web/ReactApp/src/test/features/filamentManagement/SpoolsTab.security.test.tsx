/**
 * Regression test for the js/xss-through-dom CodeQL finding: the "Open
 * Spoolman" link rendered its `href` directly from the configured Spoolman
 * base URL. That URL is ultimately sourced from user-editable settings, so an
 * unvalidated value could carry a `javascript:`/`data:` scheme instead of an
 * `http(s):` one. The fix validates the scheme both before rendering the link
 * at all (`isSafeHttpUrl`) and again immediately at the `href` sink.
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import '@testing-library/jest-dom';

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

const mockGetSpools = vi.fn();
const mockGetSpoolmanConfig = vi.fn();

vi.mock('@/services/api', () => ({
  apiClient: {
    getSpools: (...args: unknown[]) => mockGetSpools(...args),
    getSpoolFilterOptions: vi.fn().mockResolvedValue({ materials: [], vendors: [], locations: [] }),
    getSpoolmanConfig: (...args: unknown[]) => mockGetSpoolmanConfig(...args),
    getSpoolmanHealth: vi.fn().mockResolvedValue({ configured: true, success: true }),
  },
}));

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

vi.mock('@/features/filamentManagement/components/SpoolCard', () => ({
  SpoolCard: () => null,
}));

vi.mock('@/features/filamentManagement/components/SpoolTableView', () => ({
  SpoolTableView: () => null,
}));

vi.mock('@/features/filamentManagement/components/SpoolCompactView', () => ({
  SpoolCompactView: () => null,
}));

vi.mock('@/features/filamentManagement/components/ColorFamilySelect', () => ({
  ColorFamilySelect: () => null,
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

import React from 'react';
import { SpoolsTab } from '@/features/filamentManagement/components/SpoolsTab';

describe('SpoolsTab — Spoolman link scheme validation', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    Object.assign(filterState, {
      search: '', material: '', vendor: '', color: '', location: '',
      showEmpty: false, pageSize: 50, sortField: 'id', sortDir: 'asc', page: 0,
    });
    mockGetSpools.mockResolvedValue({ items: [], totalCount: 0 });
  });

  it('does not render an "Open Spoolman" link for a javascript: base URL', async () => {
    mockGetSpoolmanConfig.mockResolvedValue({ baseUrl: 'javascript:alert(1)' });
    render(<SpoolsTab />);

    await waitFor(() => expect(mockGetSpoolmanConfig).toHaveBeenCalled());

    // The link must never be rendered at all for an unsafe scheme.
    expect(screen.queryByRole('link', { name: /open spoolman/i })).not.toBeInTheDocument();
  });

  it('does not render an "Open Spoolman" link for a data: base URL', async () => {
    mockGetSpoolmanConfig.mockResolvedValue({ baseUrl: 'data:text/html,<script>alert(1)</script>' });
    render(<SpoolsTab />);

    await waitFor(() => expect(mockGetSpoolmanConfig).toHaveBeenCalled());

    expect(screen.queryByRole('link', { name: /open spoolman/i })).not.toBeInTheDocument();
  });

  it('renders a safe href for a valid http(s) base URL', async () => {
    mockGetSpoolmanConfig.mockResolvedValue({ baseUrl: 'https://spoolman.local:7912' });
    render(<SpoolsTab />);

    await waitFor(() => expect(mockGetSpoolmanConfig).toHaveBeenCalled());

    const link = await screen.findByRole('link', { name: /open spoolman/i });
    expect(link).toHaveAttribute('href', 'https://spoolman.local:7912');
  });

  it('preserves an IPv6-literal base URL unmangled (decodeURI(encodeURI(...)) round-trip)', async () => {
    // Regression guard: encodeURI() alone would percent-encode the `[`/`]`
    // brackets required by an IPv6-literal host, breaking navigation for a
    // self-hosted Spoolman instance on a bracketed-literal address. The sink
    // wraps encodeURI() in decodeURI() specifically so the rendered href is
    // byte-for-byte identical to the validated input.
    mockGetSpoolmanConfig.mockResolvedValue({ baseUrl: 'http://[::1]:7912' });
    render(<SpoolsTab />);

    await waitFor(() => expect(mockGetSpoolmanConfig).toHaveBeenCalled());

    const link = await screen.findByRole('link', { name: /open spoolman/i });
    expect(link).toHaveAttribute('href', 'http://[::1]:7912');
  });
});
