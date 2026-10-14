import type { TourStepDefinition } from '@/common/hooks/usePageTour';

export const printersTour: TourStepDefinition[] = [
  {
    element: '[data-tour="printers-actions"]',
    popover: {
      title: 'Add & Discover Printers',
      description:
        'Add printers manually or use network discovery to find them automatically. Import/export lets you back up your fleet configuration.',
    },
  },
  {
    element: '[data-tour="printers-filters"]',
    popover: {
      title: 'Filter & View Modes',
      description:
        'Use the funnel button to show State, Backend, and Done in filters. Changes update the fleet immediately. Closing the strip keeps filters active; use the clear-filters button beside the funnel to reset them. Sort and view controls stay available independently.',
    },
  },
  {
    element: '[data-tour="printers-grid"]',
    popover: {
      title: 'Your Printer Fleet',
      description:
        'Each card shows a printer\'s name, status, and live progress. Printers needing attention (bed clear, errors) sort to the top automatically.',
    },
  },
  {
    element: '[data-tour="printers-card"]',
    popover: {
      title: 'Printer Card Actions',
      description:
        'Click a card to open the detail sidebar with temperatures, controls, and job info. Use the menu for editing, maintenance, or deletion.',
    },
  },
];
