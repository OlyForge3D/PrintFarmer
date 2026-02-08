import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { BrowserRouter } from 'react-router';
import { QueryClientProvider, QueryClient } from '@tanstack/react-query';
import { GcodeLibraryPage } from '@/features/gcode/pages/GcodeLibraryPage';

vi.mock('@/features/gcode/components/GcodeFileBrowser', () => ({
  GcodeFileBrowser: () => <div>GcodeFileBrowser</div>
}));

vi.mock('@/common/hooks/useKeyboardShortcuts', () => ({
  useKeyboardShortcuts: vi.fn()
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
});
