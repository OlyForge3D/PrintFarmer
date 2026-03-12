import type { TourStepDefinition } from '@/common/hooks/usePageTour';

export const printQueueTour: TourStepDefinition[] = [
  {
    element: '[data-tour="queue-stats"]',
    popover: {
      title: 'Queue Overview',
      description:
        'See how many jobs are queued, printing, paused, and the average wait time. These update in real-time so you always know your farm\'s throughput.',
    },
  },
  {
    element: '[data-tour="queue-filters"]',
    popover: {
      title: 'Filters & Auto-Dispatch',
      description:
        'Filter jobs by status, model, or material. The auto-dispatch toggle controls whether jobs are sent to printers automatically or require manual dispatch.',
    },
  },
  {
    element: '[data-tour="queue-jobs-table"]',
    popover: {
      title: 'Job Queue',
      description:
        'Each row is a print job with its status, assigned printer, and priority. Drag to reorder, or use the actions menu to dispatch, pause, or cancel jobs.',
    },
  },
  {
    element: '[data-tour="queue-tabs"]',
    popover: {
      title: 'Queue, History & Dispatch Log',
      description:
        'Switch between active queue, completed job history, and the dispatch log that shows every auto-dispatch decision the system made.',
    },
  },
];
