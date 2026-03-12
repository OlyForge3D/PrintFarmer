import type { TourStepDefinition } from '@/common/hooks/usePageTour';

export const settingsTour: TourStepDefinition[] = [
  {
    element: '[data-tour="settings-nav"]',
    popover: {
      title: 'Settings Sections',
      description:
        'Use this sidebar to jump between settings groups. Sections are organized by category — General, Network, Slicing, and more. The active section highlights as you scroll.',
    },
  },
  {
    element: '[data-tour="settings-content"]',
    popover: {
      title: 'Configuration Options',
      description:
        'Each section contains the settings for that category. Fields are validated as you type — look for red error messages if a value is out of range or missing.',
    },
  },
  {
    element: '[data-tour="settings-save"]',
    popover: {
      title: 'Save Your Changes',
      description:
        'After making changes, click "Save All" to apply them. All sections are saved together. If there are validation errors, you\'ll be told exactly which fields need fixing.',
    },
  },
];
