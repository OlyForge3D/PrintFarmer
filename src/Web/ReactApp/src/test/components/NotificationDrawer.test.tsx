import '@testing-library/jest-dom';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { NotificationDrawer } from '@/common/components/NotificationDrawer';
import { NotificationDto, NotificationType } from '@/types/api';

// Mock date-fns
vi.mock('date-fns', () => ({
  formatDistanceToNow: () => '5 minutes ago',
}));

// Mock the API hooks
vi.mock('@/common/hooks/useApi', () => ({
  useNotifications: vi.fn(),
  useMarkNotificationAsRead: vi.fn(),
  useMarkAllNotificationsAsRead: vi.fn(),
  useDeleteNotification: vi.fn(),
}));

// Dynamic import after mocks
const {
  useNotifications,
  useMarkNotificationAsRead,
  useMarkAllNotificationsAsRead,
  useDeleteNotification,
} = await import('@/common/hooks/useApi');

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

const mockNotifications: NotificationDto[] = [
  {
    id: '1',
    subject: 'Job completed',
    body: 'Print job "test-model.gcode" completed successfully',
    type: NotificationType.JobCompleted,
    isRead: false,
    createdAt: new Date('2024-01-15T10:00:00Z').toISOString(),
    jobId: 'job-1',
  },
  {
    id: '2',
    subject: 'Job failed',
    body: 'Print job "failed-model.gcode" failed',
    type: NotificationType.JobFailed,
    isRead: true,
    createdAt: new Date('2024-01-15T09:00:00Z').toISOString(),
    jobId: 'job-2',
  },
  {
    id: '3',
    subject: 'System alert',
    body: 'Low filament warning',
    type: NotificationType.SystemAlert,
    isRead: false,
    createdAt: new Date('2024-01-15T08:00:00Z').toISOString(),
    jobId: null,
  },
];

describe('NotificationDrawer', () => {
  const mockMarkAsRead = vi.fn();
  const mockMarkAllAsRead = vi.fn();
  const mockDelete = vi.fn();
  const mockRefetch = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
    mockMarkAsRead.mockResolvedValue(undefined);
    mockMarkAllAsRead.mockResolvedValue(undefined);
    mockDelete.mockResolvedValue(undefined);

    vi.mocked(useNotifications).mockReturnValue({
      data: [],
      refetch: mockRefetch,
    } as ReturnType<typeof useNotifications>);

    vi.mocked(useMarkNotificationAsRead).mockReturnValue({
      mutateAsync: mockMarkAsRead,
      isPending: false,
    } as ReturnType<typeof useNotifications>);

    vi.mocked(useMarkAllNotificationsAsRead).mockReturnValue({
      mutateAsync: mockMarkAllAsRead,
      isPending: false,
    } as ReturnType<typeof useNotifications>);

    vi.mocked(useDeleteNotification).mockReturnValue({
      mutateAsync: mockDelete,
      isPending: false,
    } as ReturnType<typeof useNotifications>);
  });

  it('does not render when closed', () => {
    render(
      <TestWrapper>
        <NotificationDrawer isOpen={false} onClose={vi.fn()} />
      </TestWrapper>
    );

    expect(screen.queryByRole('heading', { name: 'Notifications' })).not.toBeInTheDocument();
  });

  it('renders empty state when no notifications', () => {
    vi.mocked(useNotifications).mockReturnValue({
      data: [],
      refetch: mockRefetch,
    } as ReturnType<typeof useNotifications>);

    render(
      <TestWrapper>
        <NotificationDrawer isOpen={true} onClose={vi.fn()} />
      </TestWrapper>
    );

    expect(screen.getByRole('heading', { name: 'Notifications' })).toBeInTheDocument();
    expect(screen.getByText('No notifications')).toBeInTheDocument();
    expect(screen.getByText('📭')).toBeInTheDocument();
  });

  it('renders notification list with unread and read notifications', () => {
    vi.mocked(useNotifications).mockReturnValue({
      data: mockNotifications,
      refetch: mockRefetch,
    } as ReturnType<typeof useNotifications>);

    render(
      <TestWrapper>
        <NotificationDrawer isOpen={true} onClose={vi.fn()} />
      </TestWrapper>
    );

    expect(screen.getByText('Job completed')).toBeInTheDocument();
    expect(screen.getByText('Job failed')).toBeInTheDocument();
    expect(screen.getByText('System alert')).toBeInTheDocument();

    // Check for unread indicators
    const unreadIndicators = screen.getAllByTitle('Unread');
    expect(unreadIndicators).toHaveLength(2); // Two unread notifications
  });

  it('shows "Mark all as read" button when there are unread notifications', () => {
    vi.mocked(useNotifications).mockReturnValue({
      data: mockNotifications,
      refetch: mockRefetch,
    } as ReturnType<typeof useNotifications>);

    render(
      <TestWrapper>
        <NotificationDrawer isOpen={true} onClose={vi.fn()} />
      </TestWrapper>
    );

    expect(screen.getByRole('button', { name: /mark all as read/i })).toBeInTheDocument();
  });

  it('does not show "Mark all as read" button when all notifications are read', () => {
    const allReadNotifications = mockNotifications.map(n => ({ ...n, isRead: true }));
    vi.mocked(useNotifications).mockReturnValue({
      data: allReadNotifications,
      refetch: mockRefetch,
    } as ReturnType<typeof useNotifications>);

    render(
      <TestWrapper>
        <NotificationDrawer isOpen={true} onClose={vi.fn()} />
      </TestWrapper>
    );

    expect(screen.queryByRole('button', { name: /mark all as read/i })).not.toBeInTheDocument();
  });

  it('marks individual notification as read when clicked', async () => {
    const user = userEvent.setup();
    vi.mocked(useNotifications).mockReturnValue({
      data: mockNotifications,
      refetch: mockRefetch,
    } as ReturnType<typeof useNotifications>);

    render(
      <TestWrapper>
        <NotificationDrawer isOpen={true} onClose={vi.fn()} />
      </TestWrapper>
    );

    // Click on an unread notification
    const notification = screen.getByText('Job completed');
    await user.click(notification);

    await waitFor(() => {
      expect(mockMarkAsRead).toHaveBeenCalledWith('1');
    });
  });

  it('does not mark already read notification when clicked', async () => {
    const user = userEvent.setup();
    vi.mocked(useNotifications).mockReturnValue({
      data: mockNotifications,
      refetch: mockRefetch,
    } as ReturnType<typeof useNotifications>);

    render(
      <TestWrapper>
        <NotificationDrawer isOpen={true} onClose={vi.fn()} />
      </TestWrapper>
    );

    // Click on a read notification
    const notification = screen.getByText('Job failed');
    await user.click(notification);

    await waitFor(() => {
      expect(mockMarkAsRead).not.toHaveBeenCalled();
    });
  });

  it('marks all notifications as read when button clicked', async () => {
    const user = userEvent.setup();
    vi.mocked(useNotifications).mockReturnValue({
      data: mockNotifications,
      refetch: mockRefetch,
    } as ReturnType<typeof useNotifications>);

    render(
      <TestWrapper>
        <NotificationDrawer isOpen={true} onClose={vi.fn()} />
      </TestWrapper>
    );

    const markAllButton = screen.getByRole('button', { name: /mark all as read/i });
    await user.click(markAllButton);

    await waitFor(() => {
      expect(mockMarkAllAsRead).toHaveBeenCalledWith(['1', '3']); // Only unread notification IDs
    });
  });

  it('deletes notification when delete button clicked', async () => {
    const user = userEvent.setup();
    vi.mocked(useNotifications).mockReturnValue({
      data: [mockNotifications[0]],
      refetch: mockRefetch,
    } as ReturnType<typeof useNotifications>);

    render(
      <TestWrapper>
        <NotificationDrawer isOpen={true} onClose={vi.fn()} />
      </TestWrapper>
    );

    // Find delete button (aria-label="Delete notification")
    const deleteButtons = screen.getAllByLabelText('Delete notification');
    await user.click(deleteButtons[0]);

    await waitFor(() => {
      expect(mockDelete).toHaveBeenCalledWith('1');
    });
  });

  it('closes drawer when backdrop is clicked', async () => {
    const user = userEvent.setup();
    const mockOnClose = vi.fn();
    vi.mocked(useNotifications).mockReturnValue({
      data: [],
      refetch: mockRefetch,
    } as ReturnType<typeof useNotifications>);

    render(
      <TestWrapper>
        <NotificationDrawer isOpen={true} onClose={mockOnClose} />
      </TestWrapper>
    );

    const backdrop = document.querySelector('.fixed.inset-0.bg-black\\/50');
    expect(backdrop).toBeInTheDocument();

    await user.click(backdrop!);

    await waitFor(() => {
      expect(mockOnClose).toHaveBeenCalled();
    });
  });

  it('closes drawer when close button clicked', async () => {
    const user = userEvent.setup();
    const mockOnClose = vi.fn();
    vi.mocked(useNotifications).mockReturnValue({
      data: [],
      refetch: mockRefetch,
    } as ReturnType<typeof useNotifications>);

    render(
      <TestWrapper>
        <NotificationDrawer isOpen={true} onClose={mockOnClose} />
      </TestWrapper>
    );

    const closeButton = screen.getByLabelText('Close notifications');
    await user.click(closeButton);

    await waitFor(() => {
      expect(mockOnClose).toHaveBeenCalled();
    });
  });

  it('refetches notifications when drawer opens', () => {
    const { rerender } = render(
      <TestWrapper>
        <NotificationDrawer isOpen={false} onClose={vi.fn()} />
      </TestWrapper>
    );

    expect(mockRefetch).not.toHaveBeenCalled();

    rerender(
      <TestWrapper>
        <NotificationDrawer isOpen={true} onClose={vi.fn()} />
      </TestWrapper>
    );

    expect(mockRefetch).toHaveBeenCalled();
  });

  it('displays correct icon for each notification type', () => {
    const typeNotifications: NotificationDto[] = [
      { ...mockNotifications[0], type: NotificationType.JobCompleted },
      { ...mockNotifications[1], id: '4', type: NotificationType.JobFailed },
      { ...mockNotifications[2], id: '5', type: NotificationType.JobStarted },
      { ...mockNotifications[0], id: '6', type: NotificationType.JobPaused },
      { ...mockNotifications[0], id: '7', type: NotificationType.JobResumed },
      { ...mockNotifications[0], id: '8', type: NotificationType.QueueAlert },
      { ...mockNotifications[0], id: '9', type: NotificationType.SystemAlert },
    ];

    vi.mocked(useNotifications).mockReturnValue({
      data: typeNotifications,
      refetch: mockRefetch,
    } as ReturnType<typeof useNotifications>);

    render(
      <TestWrapper>
        <NotificationDrawer isOpen={true} onClose={vi.fn()} />
      </TestWrapper>
    );

    expect(screen.getByText('✅')).toBeInTheDocument(); // JobCompleted
    expect(screen.getByText('❌')).toBeInTheDocument(); // JobFailed
    expect(screen.getAllByText('▶️')).toHaveLength(2); // JobStarted, JobResumed
    expect(screen.getByText('⏸️')).toBeInTheDocument(); // JobPaused
    expect(screen.getByText('⚠️')).toBeInTheDocument(); // QueueAlert
    expect(screen.getByText('🔔')).toBeInTheDocument(); // SystemAlert
  });
});
