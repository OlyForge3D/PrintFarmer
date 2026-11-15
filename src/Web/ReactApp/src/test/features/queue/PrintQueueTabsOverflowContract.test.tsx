import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { describe, expect, it, vi } from 'vitest';
import { PrintQueueDashboardPage } from '@/features/queue/pages/PrintQueueDashboardPage';

vi.mock('@tanstack/react-query', () => ({
  useQuery: () => ({ data: undefined, isLoading: false, isFetching: false, error: null }),
  useQueryClient: () => ({ invalidateQueries: vi.fn() }),
}));

vi.mock('@/common/components/PageTemplate', () => ({
  PageTemplate: ({ children }: { children?: import('react').ReactNode }) => children ?? null,
}));
vi.mock('@/common/components/ui/Alert', () => ({ Alert: () => null }));
vi.mock('@/common/components/modals/ConfirmationModal', () => ({ ConfirmationModal: () => null }));
vi.mock('@/common/components/HelpButton', () => ({ HelpButton: () => null }));
vi.mock('@/common/hooks/useKeyboardNavigation', () => ({
  useKeyboardNavigation: () => ({ selectedIndex: -1 }),
}));
vi.mock('@/common/hooks/useKeyboardShortcuts', () => ({ useKeyboardShortcuts: () => undefined }));
vi.mock('@/common/hooks/usePageTour', () => ({ usePageTour: () => ({ startTour: () => undefined }) }));
vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasPermission: () => false }),
}));
vi.mock('@/features/queue/components/QueueFiltersBar', () => ({ TableFiltersBar: () => null }));
vi.mock('@/features/queue/components/QueueJobsTable', () => ({ QueueJobsTable: () => null }));
vi.mock('@/features/queue/components/QueueJobsCollectionViews', () => ({
  QueueJobsCardView: () => null,
  QueueJobsListView: () => null,
}));
vi.mock('@/features/queue/components/QueueViewModeSelector', () => ({
  QueueViewModeSelector: () => null,
}));
vi.mock('@/features/queue/components/JobDetailsModal', () => ({ default: () => null }));
vi.mock('@/features/queue/components/QueueHistoryTab', () => ({ default: () => null }));
vi.mock('@/features/queue/components/DispatchLogTab', () => ({ default: () => null }));
vi.mock('@/features/queue/components/QueueTimelineTab', () => ({ default: () => null }));
vi.mock('@/features/queue/components/SpoolValidationModal', () => ({ SpoolValidationModal: () => null }));
vi.mock('@/features/queue/components/AutoDispatchGlobalToggle', () => ({
  AutoDispatchGlobalToggle: () => null,
}));
vi.mock('@/features/scheduling/components/ScheduleModal', () => ({ ScheduleModal: () => null }));
vi.mock('@/services/api', () => ({ apiClient: {} }));
vi.mock('@/services/printer-signalr', () => ({
  printerSignalRService: {
    connect: () => undefined,
    onDispatchUploadProgress: () => () => undefined,
    getLastStatuses: () => new Map(),
    onPrinterStatusUpdate: () => () => undefined,
  },
}));
vi.mock('@/features/printers/hooks/useQueueSummariesFleet', () => ({
  queueSummariesFleetQueryKey: ['queue-summaries-fleet'],
}));
vi.mock('@/features/queue/hooks/useQueueReordering', () => ({
  useQueueReordering: () => ({
    jobs: [],
    announcement: '',
    isMoving: false,
    draggedJobId: null,
    moveJob: () => undefined,
    startDraggingJob: () => undefined,
    stopDraggingJob: () => undefined,
    canDropOnJob: () => false,
    reorderNeighbors: undefined,
  }),
}));

describe('PrintQueueDashboardPage tab strip overflow contract (issue #1754)', () => {
  it('keeps the tablist stable while date controls stay inside dated panels', () => {
    render(
      <MemoryRouter initialEntries={['/printQueue/print-queue']}>
        <Routes>
          <Route path="/printQueue/:tabId" element={<PrintQueueDashboardPage />} />
        </Routes>
      </MemoryRouter>,
    );

    const tablist = screen.getByRole('tablist');
    const tabs = within(tablist).getAllByRole('tab');
    expect(tabs.map((tab) => tab.textContent?.trim())).toEqual([
      'Print Queue',
      'Timeline',
      'History',
      'Dispatch Log',
    ]);
    expect(tablist).toHaveClass('overflow-x-auto');
    expect(within(screen.getByRole('tabpanel')).queryByRole('group', { name: 'Queue date controls' }))
      .not.toBeInTheDocument();

    for (const [tabName, panelId] of [
      ['Timeline', 'timeline'],
      ['History', 'history'],
      ['Dispatch Log', 'dispatch-log'],
    ]) {
      fireEvent.click(screen.getByRole('tab', { name: tabName }));

      expect(screen.getByRole('tablist')).toBe(tablist);
      expect(within(tablist).getAllByRole('tab').map((tab) => tab.textContent?.trim())).toEqual([
        'Print Queue',
        'Timeline',
        'History',
        'Dispatch Log',
      ]);

      const panel = screen.getByRole('tabpanel');
      expect(panel).toHaveAttribute('id', `panel-${panelId}`);
      expect(within(panel).getByRole('group', { name: 'Queue date controls' })).toBeInTheDocument();
      expect(within(panel).getByText('Date range')).toBeInTheDocument();
    }

    fireEvent.click(screen.getByRole('tab', { name: 'Print Queue' }));
    const queuePanel = screen.getByRole('tabpanel');
    expect(queuePanel).toHaveAttribute('id', 'panel-print-queue');
    expect(within(queuePanel).queryByRole('group', { name: 'Queue date controls' }))
      .not.toBeInTheDocument();
  });
});
