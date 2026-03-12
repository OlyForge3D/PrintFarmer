import type { TourStepDefinition } from '@/common/hooks/usePageTour';

export const gcodeLibraryTour: TourStepDefinition[] = [
  {
    element: '[data-tour="gcode-toolbar"]',
    popover: {
      title: 'Search & Sort Your Files',
      description:
        'Use the search bar to find files by name, and the sort dropdown to order by name, size, or date. The view toggle on the right switches between grid cards and an explorer-style list.',
    },
  },
  {
    element: '[data-tour="gcode-upload"]',
    popover: {
      title: 'Upload G-code Files',
      description:
        'Click the upload button to add new G-code files to your library. You can drag-and-drop multiple files at once. Files are automatically analyzed for material, nozzle, and temperature data.',
    },
  },
  {
    element: '[data-tour="gcode-filters"]',
    popover: {
      title: 'Filter by Tags & Printer Model',
      description:
        'Open the filter panel to narrow your file list by tags or compatible printer model. Useful when you have hundreds of files and need to find the right one fast.',
    },
  },
  {
    element: '[data-tour="gcode-file-list"]',
    popover: {
      title: 'Your G-code Files',
      description:
        'Each file shows extracted metadata — material type, nozzle size, and print temperatures. Select files with checkboxes to tag or delete them in bulk.',
    },
  },
  {
    element: '[data-tour="gcode-fab"]',
    popover: {
      title: 'Quick Upload',
      description:
        'This floating button gives you one-tap access to upload new G-code files from anywhere on the page. Same as the toolbar upload, just faster to reach.',
    },
  },
];
