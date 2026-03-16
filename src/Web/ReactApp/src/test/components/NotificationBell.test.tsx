import '@testing-library/jest-dom';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { NotificationBell } from '@/common/components/NotificationBell';

// Mock the API hooks
vi.mock('@/common/hooks/useApi', () => ({
  useUnreadCount: vi.fn(),
}));

// Mock NotificationDrawer to isolate NotificationBell tests
vi.mock('@/common/components/NotificationDrawer', () => ({
  NotificationDrawer: ({ isOpen, onClose }: { isOpen: boolean; onClose: () => void }) => (
    isOpen ? (
      <div data-testid="notification-drawer">
        <button onClick={onClose} data-testid="close-drawer">Close</button>
      </div>
    ) : null
  ),
}));

// Dynamic import after mocks
const { useUnreadCount } = await import('@/common/hooks/useApi');

function TestWrapper({ children }: { children: React.ReactNode }) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
      },
    },
  });

  return (
    <QueryClientProvider client={queryClient}>
      {children}
    </QueryClientProvider>
  );
}

describe('NotificationBell', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders bell icon without badge when no unread notifications', () => {
    vi.mocked(useUnreadCount).mockReturnValue({ data: 0 } as ReturnType<typeof useUnreadCount>);

    render(
      <TestWrapper>
        <NotificationBell />
      </TestWrapper>
    );

    const button = screen.getByRole('button', { name: 'Notifications' });
    expect(button).toBeInTheDocument();
    expect(button).toHaveAttribute('title', 'Notifications');

    // Badge should not be present
    const badge = button.querySelector('span.bg-pf-accent');
    expect(badge).not.toBeInTheDocument();
  });

  it('renders bell icon with unread count badge for single notification', () => {
    vi.mocked(useUnreadCount).mockReturnValue({ data: 1 } as ReturnType<typeof useUnreadCount>);

    render(
      <TestWrapper>
        <NotificationBell />
      </TestWrapper>
    );

    const button = screen.getByRole('button', { name: 'Notifications (1 unread)' });
    expect(button).toBeInTheDocument();
    expect(button).toHaveAttribute('title', '1 unread notification');

    // Badge should show "1"
    const badge = button.querySelector('span.bg-pf-accent');
    expect(badge).toBeInTheDocument();
    expect(badge).toHaveTextContent('1');
  });

  it('renders bell icon with unread count badge for multiple notifications', () => {
    vi.mocked(useUnreadCount).mockReturnValue({ data: 5 } as ReturnType<typeof useUnreadCount>);

    render(
      <TestWrapper>
        <NotificationBell />
      </TestWrapper>
    );

    const button = screen.getByRole('button', { name: 'Notifications (5 unread)' });
    expect(button).toBeInTheDocument();
    expect(button).toHaveAttribute('title', '5 unread notifications');

    // Badge should show "5"
    const badge = button.querySelector('span.bg-pf-accent');
    expect(badge).toBeInTheDocument();
    expect(badge).toHaveTextContent('5');
  });

  it('renders badge with "99+" for count over 99', () => {
    vi.mocked(useUnreadCount).mockReturnValue({ data: 150 } as ReturnType<typeof useUnreadCount>);

    render(
      <TestWrapper>
        <NotificationBell />
      </TestWrapper>
    );

    const button = screen.getByRole('button');
    const badge = button.querySelector('span.bg-pf-accent');
    expect(badge).toBeInTheDocument();
    expect(badge).toHaveTextContent('99+');
  });

  it('opens notification drawer when clicked', async () => {
    const user = userEvent.setup();
    vi.mocked(useUnreadCount).mockReturnValue({ data: 3 } as ReturnType<typeof useUnreadCount>);

    render(
      <TestWrapper>
        <NotificationBell />
      </TestWrapper>
    );

    const button = screen.getByRole('button');
    expect(screen.queryByTestId('notification-drawer')).not.toBeInTheDocument();

    await user.click(button);

    await waitFor(() => {
      expect(screen.getByTestId('notification-drawer')).toBeInTheDocument();
    });
  });

  it('closes notification drawer when onClose is called', async () => {
    const user = userEvent.setup();
    vi.mocked(useUnreadCount).mockReturnValue({ data: 0 } as ReturnType<typeof useUnreadCount>);

    render(
      <TestWrapper>
        <NotificationBell />
      </TestWrapper>
    );

    // Open drawer
    const bellButton = screen.getByRole('button', { name: 'Notifications' });
    await user.click(bellButton);

    await waitFor(() => {
      expect(screen.getByTestId('notification-drawer')).toBeInTheDocument();
    });

    // Close drawer
    const closeButton = screen.getByTestId('close-drawer');
    await user.click(closeButton);

    await waitFor(() => {
      expect(screen.queryByTestId('notification-drawer')).not.toBeInTheDocument();
    });
  });

  it('handles undefined unread count gracefully', () => {
    vi.mocked(useUnreadCount).mockReturnValue({ data: undefined } as ReturnType<typeof useUnreadCount>);

    render(
      <TestWrapper>
        <NotificationBell />
      </TestWrapper>
    );

    const button = screen.getByRole('button', { name: 'Notifications' });
    expect(button).toBeInTheDocument();

    // Badge should not be present when count is undefined
    const badge = button.querySelector('span.bg-pf-accent');
    expect(badge).not.toBeInTheDocument();
  });
});
