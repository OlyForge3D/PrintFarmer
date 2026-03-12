import type { TourStepDefinition } from '@/common/hooks/usePageTour';

export const dashboardTour: TourStepDefinition[] = [
  {
    element: '[data-tour="stats-cards"]',
    popover: {
      title: 'Farm Status at a Glance',
      description:
        'These cards show your fleet health — total printers, how many are online, actively printing, paused, offline, or in maintenance.',
    },
  },
  {
    element: '[data-tour="active-jobs"]',
    popover: {
      title: 'Active Print Jobs',
      description:
        'See what\'s printing right now across all printers. Click a job to view progress, temperatures, and controls.',
    },
  },
  {
    element: '[data-tour="recent-prints"]',
    popover: {
      title: 'Recent Prints',
      description:
        'A quick history of completed and failed prints. Use this to spot recurring failures or track throughput.',
    },
  },
  {
    element: '[data-tour="tasks-widget"]',
    popover: {
      title: 'Tasks & To-Dos',
      description:
        'Outstanding tasks that need attention — printer maintenance due, filament running low, or actions queued by the system.',
    },
  },
  {
    element: '[data-tour="services-widget"]',
    popover: {
      title: 'Background Services',
      description:
        'Monitor the health of background services like printer polling, discovery, and slicer workers. Green means healthy.',
    },
  },
];
