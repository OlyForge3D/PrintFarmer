import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { GcodeFileBrowser } from '@/features/gcode/components/GcodeFileBrowser';

const auth = vi.hoisted(() => ({ grants: new Set<string>() }));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({
    hasPermission: (resource: string, action: string) => auth.grants.has(`${resource}:${action}`),
  }),
}));

vi.mock('@/features/fileBrowser/components/FileBrowser', () => ({
  FileBrowser: ({ extraToolbarActions }: { extraToolbarActions?: React.ReactNode }) => (
    <div>{extraToolbarActions}</div>
  ),
}));

describe('GcodeFileBrowser bulk delete', () => {
  let queryClient: QueryClient;

  beforeEach(() => {
    auth.grants = new Set(['gcode_library:admin']);
    queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
      },
    });
  });

  const renderBrowser = () =>
    render(
      <QueryClientProvider client={queryClient}>
        <GcodeFileBrowser selectedFileIds={['first.gcode', 'second.gcode']} />
      </QueryClientProvider>,
    );

  it('uses the semantic error foreground without replacing the secondary hover surface', () => {
    renderBrowser();

    const button = screen.getByRole('button', { name: 'Delete (2)' });
    expect(button).toHaveClass('text-pf-error-text!');
    expect(button).not.toHaveClass('text-pf-error');
    expect(button).not.toHaveClass('hover:text-pf-error');
    expect(button).not.toHaveClass('hover:bg-pf-error/10');
  });

  it('keeps the selected-file count in its accessible name and opens confirmation', () => {
    renderBrowser();

    fireEvent.click(screen.getByRole('button', { name: 'Delete (2)' }));

    expect(screen.getByRole('dialog', { name: 'Delete Selected Files' })).toBeInTheDocument();
    expect(
      screen.getByText(
        'Are you sure you want to delete 2 selected files? This action cannot be undone.',
      ),
    ).toBeInTheDocument();
  });

  it('hides bulk delete without the library admin grant the delete API requires', () => {
    auth.grants = new Set(['gcode_library:read', 'gcode_library:create']);
    renderBrowser();

    expect(screen.queryByRole('button', { name: 'Delete (2)' })).not.toBeInTheDocument();
  });
});
