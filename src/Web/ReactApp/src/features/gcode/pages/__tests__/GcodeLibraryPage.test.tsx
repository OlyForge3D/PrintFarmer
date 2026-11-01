import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { BrowserRouter } from 'react-router';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import type { FileBrowserFilterActions } from '@/features/fileBrowser/types';
import { GcodeLibraryPage } from '@/features/gcode/pages/GcodeLibraryPage';
import { useKeyboardShortcuts } from '@/common/hooks/useKeyboardShortcuts';

vi.mock('@/features/gcode/components/GcodeFileBrowser', () => ({
  GcodeFileBrowser: ({ filterActions }: { filterActions?: FileBrowserFilterActions }) => (
    <div>
      <div>GcodeFileBrowser</div>
      {filterActions?.(trigger => <div className="pf-toolbar__controls">{trigger}</div>)}
    </div>
  )
}));

vi.mock('@/common/hooks/useKeyboardShortcuts', () => ({
  useKeyboardShortcuts: vi.fn()
}));

vi.mock('@/components/TagInput', () => ({
  default: ({ selectedTags, onChange }: {
    selectedTags: Array<{ id: string; name: string }>;
    onChange: (tags: Array<{ id: string; name: string }>) => void;
  }) => (
    <button
      type="button"
      onClick={() => onChange(selectedTags.length ? [] : [{ id: 'tag-1', name: 'PLA' }])}
    >
      Toggle tag
    </button>
  )
}));

vi.mock('@/services/api', () => ({
  apiClient: {
    getTags: vi.fn().mockResolvedValue([{ id: 'tag-1', name: 'PLA', color: '#000000' }]),
  },
}));

const queryClient = new QueryClient({
  defaultOptions: {
    queries: { retry: false },
  },
});

const renderComponent = () => {
  return render(
    <BrowserRouter>
      <QueryClientProvider client={queryClient}>
        <GcodeLibraryPage />
      </QueryClientProvider>
    </BrowserRouter>
  );
};

describe('GcodeLibraryPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    queryClient.clear();
  });

  it('should render without crashing', () => {
    renderComponent();
    expect(screen.getByText('GcodeFileBrowser')).toBeInTheDocument();
  });

  it('should display GcodeFileBrowser component', () => {
    renderComponent();
    expect(screen.getByText('GcodeFileBrowser')).toBeInTheDocument();
  });

  it('should render floating action button', () => {
    renderComponent();
    const fabButton = screen.getByRole('button', { name: /Upload G-Code/i });
    expect(fabButton).toBeInTheDocument();
  });

  it('discloses filters, updates the active count, and resets the selection', async () => {
    renderComponent();
    const filtersButton = screen.getByRole('button', { name: 'Filters' });
    const panelId = filtersButton.getAttribute('aria-controls') ?? '';
    const panel = document.getElementById(panelId);
    expect(filtersButton).toHaveAttribute('aria-expanded', 'false');
    expect(panelId).not.toBe('');
    expect(panel).not.toBeNull();
    expect(panel?.getAttribute('role')).toBe('region');
    expect(panel?.getAttribute('aria-label')).toBe('Filters');
    expect(panel).toHaveAttribute('hidden');
    fireEvent.click(filtersButton);
    expect(panel).not.toHaveAttribute('hidden');
    fireEvent.click(await screen.findByRole('button', { name: 'Toggle tag' }));
    expect(screen.getByRole('button', { name: 'Filters (1)' })).toHaveAttribute('aria-expanded', 'true');
    fireEvent.click(screen.getByRole('button', { name: 'Reset filters' }));
    expect(screen.getByRole('button', { name: 'Filters' })).toHaveAttribute('aria-expanded', 'true');
  });

  it('toggles the shared filter disclosure with the f shortcut', () => {
    renderComponent();
    const shortcuts = vi.mocked(useKeyboardShortcuts).mock.calls.at(-1)?.[0];
    act(() => shortcuts?.find((shortcut) => shortcut.key === 'f')?.handler());
    expect(screen.getByRole('button', { name: 'Filters' })).toHaveAttribute('aria-expanded', 'true');
  });
});
