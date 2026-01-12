/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    "./src/**/*.{js,jsx,ts,tsx}",
    "./index.html",
  ],
  theme: {
    extend: {
      colors: {
        // PrintFarmer Colors - Now using CSS Custom Properties for dynamic theming
        'pf': {
          // Primary backgrounds
          'bg-0': 'var(--pf-bg-0)',           // Main background
          'bg-1': 'var(--pf-bg-1)',           // Secondary background  
          'bg-2': 'var(--pf-bg-2)',           // Tertiary background
          'panel': 'var(--pf-panel)',         // Panel background
          
          // Borders and dividers
          'border': 'var(--pf-border)',       // Main border color
          'border-light': 'var(--pf-border-light)',
          'border-medium': 'var(--pf-border-medium)',
          'border-dark': 'var(--pf-border-dark)',
          'border-gray': 'var(--pf-border-gray)',
          
          // Text colors
          'text-primary': 'var(--pf-text-primary)',
          'text-secondary': 'var(--pf-text-secondary)',
          'text-tertiary': 'var(--pf-text-tertiary)',
          'text-light': 'var(--pf-text-light)',
          'text-muted': 'var(--pf-text-muted)',
          
          // Accent colors
          'accent': 'var(--pf-accent)',
          'accent-bg': 'var(--pf-accent-bg)',
          'accent-hover': 'var(--pf-accent-hover)',
          'accent-2': 'var(--pf-accent-2)',
          'success': 'var(--pf-success)',
          'success-bg': 'var(--pf-success-bg)',
          'success-hover': 'var(--pf-success-hover)',
          'link': 'var(--pf-link)',
          
          // Status colors
          'status-online-bg': 'var(--pf-status-online-bg)',
          'status-online-text': 'var(--pf-status-online-text)', 
          'status-online-border': 'var(--pf-status-online-border)',
          'status-offline-bg': 'var(--pf-status-offline-bg)',
          'status-offline-text': 'var(--pf-status-offline-text)',
          'status-offline-border': 'var(--pf-status-offline-border)',
          
          // Error and warning
          'error': 'var(--pf-error)',
          'error-bg': 'var(--pf-error-bg)',
          'error-hover': 'var(--pf-error-hover)',
          'error-text': 'var(--pf-error-text)',
          'error-border': 'var(--pf-error-border)',
          'warning': 'var(--pf-warning)',
          'warning-text': 'var(--pf-warning-text)',
          
          // Loading and disabled
          'loading': 'var(--pf-loading)',
          'loading-border': 'var(--pf-loading-border)',
          'disabled': 'var(--pf-disabled)',
          
          // Gradient colors for buttons
          'gradient': {
            'primary-start': 'var(--pf-gradient-primary-start)',
            'primary-end': 'var(--pf-gradient-primary-end)',
            'secondary-start': 'var(--pf-gradient-secondary-start)',
            'secondary-end': 'var(--pf-gradient-secondary-end)',
            'success-start': 'var(--pf-gradient-success-start)',
            'success-end': 'var(--pf-gradient-success-end)',
            'green-start': 'var(--pf-gradient-green-start)',
            'green-end': 'var(--pf-gradient-green-end)',
            'green-active-start': 'var(--pf-gradient-green-active-start)',
            'green-active-end': 'var(--pf-gradient-green-active-end)',
            'gray-start': 'var(--pf-gradient-gray-start)',
            'gray-end': 'var(--pf-gradient-gray-end)',
            'gray-dark-start': 'var(--pf-gradient-gray-dark-start)',
            'gray-dark-end': 'var(--pf-gradient-gray-dark-end)',
          }
        }
      },
      fontFamily: {
        'inter': ['Inter', 'ui-sans-serif', 'system-ui'],
        'bebas': ['Bebas Neue', 'sans-serif'],
      },
      // Add theme-aware ring colors for focus states
      ringColor: {
        'pf-focus': 'var(--pf-focus-ring)',
      },
      ringOffsetColor: {
        'pf-focus': 'var(--pf-focus-ring-offset)',
      },
    },
  },
  plugins: [
    function({ addUtilities }) {
      const newUtilities = {
        '.card-container': {
          '@apply overflow-hidden flex flex-col min-h-0': {},
        },
        '.text-ellipsis': {
          '@apply truncate': {},
        },
        '.no-shrink-content': {
          '@apply min-w-0': {},
        },
      };
      addUtilities(newUtilities);
    },
  ],
  safelist: [
    // PrintFarmer specific colors - Backgrounds
    'bg-pf-bg-0', 'bg-pf-bg-1', 'bg-pf-bg-2', 'bg-pf-panel',
    'bg-pf-accent', 'bg-pf-accent-2', 'bg-pf-accent-bg', 'bg-pf-accent-2-hover',
    'bg-pf-success', 'bg-pf-success-bg', 'bg-pf-success-hover',
    'bg-pf-error', 'bg-pf-error-bg',
    'bg-pf-warning',
    'bg-pf-status-online-bg', 'bg-pf-status-offline-bg',
    'bg-pf-loading',
    
    // PrintFarmer specific colors - Text
    'text-pf-text-primary', 'text-pf-text-secondary', 'text-pf-text-tertiary',
    'text-pf-text-light', 'text-pf-text-muted',
    'text-pf-accent', 'text-pf-accent-2',
    'text-pf-success', 'text-pf-error', 'text-pf-error-text', 'text-pf-warning-text',
    'text-pf-status-online-text', 'text-pf-status-offline-text',
    'text-pf-link', 'text-pf-loading',
    
    // PrintFarmer specific colors - Borders
    'border-pf-border', 'border-pf-border-light', 'border-pf-border-medium', 'border-pf-border-dark', 'border-pf-border-gray',
    'border-pf-accent', 'border-pf-accent-2',
    'border-pf-success', 'border-pf-error', 'border-pf-error-border',
    'border-pf-status-online-border', 'border-pf-status-offline-border',
    'border-pf-loading', 'border-pf-loading-border',
    
    // Hover states for PrintFarmer colors
    'hover:bg-pf-bg-0', 'hover:bg-pf-bg-1', 'hover:bg-pf-bg-2',
    'hover:bg-pf-accent', 'hover:bg-pf-accent-2', 'hover:bg-pf-accent-2-hover',
    'hover:bg-pf-success', 'hover:bg-pf-success-hover',
    'hover:bg-pf-error',
    'hover:text-pf-text-primary', 'hover:text-pf-text-secondary',
    'hover:text-pf-accent', 'hover:text-pf-accent-2',
    'hover:text-pf-success',
    'hover:border-pf-border', 'hover:border-pf-accent', 'hover:border-pf-accent-2',
    
    // Checkbox/Radio accent colors
    'accent-pf-accent', 'accent-pf-accent-2', 'accent-pf-success',
    
    // Traditional Tailwind colors (for fallback)
    'bg-white', 'bg-gray-100', 'bg-gray-200', 'bg-gray-800', 'bg-gray-900',
    'bg-blue-600', 'bg-green-600', 'bg-red-600', 'bg-yellow-600',
    'text-white', 'text-gray-100', 'text-gray-200', 'text-gray-300',
    'text-gray-400', 'text-gray-500', 'text-gray-600', 'text-gray-700',
    'text-gray-800', 'text-gray-900', 'text-blue-600', 'text-green-600',
    
    // Hover states
    'hover:bg-gray-50', 'hover:bg-gray-100', 'hover:bg-gray-700', 'hover:bg-blue-700',
    'hover:text-gray-600', 'hover:text-blue-700',
    
    // Border colors  
    'border-gray-200', 'border-gray-300', 'border-gray-600', 'border-blue-500',
    
    // Shadow utilities
    'shadow-sm', 'shadow', 'shadow-md', 'shadow-lg', 'shadow-xl',
    
    // Focus states
    'focus:ring-2', 'focus:ring-blue-500', 'focus:outline-none',
    
    // Rounded corners
    'rounded', 'rounded-md', 'rounded-lg', 'rounded-xl', 'rounded-full',
    
    // Padding and margins
    'p-1', 'p-2', 'p-3', 'p-4', 'p-6', 'p-8',
    'px-2', 'px-3', 'px-4', 'px-6', 'py-1', 'py-2', 'py-3', 'py-4',
    'm-1', 'm-2', 'm-3', 'mx-auto', 'mb-2', 'mb-4', 'mt-4', 'mt-8',
    
    // Width and height
    'w-full', 'w-auto', 'w-4', 'w-5', 'w-6', 'w-8', 'w-16', 'w-32',
    'h-4', 'h-5', 'h-6', 'h-8', 'h-16', 'h-32',
    
    // Flexbox and grid
    'flex', 'inline-flex', 'grid', 'flex-col', 'items-center', 'justify-center',
    'justify-between', 'gap-2', 'gap-3', 'gap-4', 'gap-6',
    'min-w-0', 'min-h-0', 'overflow-hidden', 'flex-1', 'flex-shrink-0',
    
    // Responsive grid columns
    'md:grid-cols-2', 'md:grid-cols-3', 'lg:grid-cols-3', 'lg:grid-cols-4', 'lg:grid-cols-5',
    'xl:grid-cols-3', 'xl:grid-cols-4', 'xl:grid-cols-5',
    
    // Text truncation and ellipsis
    'truncate', 'line-clamp-1', 'line-clamp-2', 'line-clamp-3',
    'whitespace-nowrap', 'break-words',
    
    // Custom utilities
    'card-container', 'text-ellipsis', 'no-shrink-content',
    
    // Font weights and sizes
    'font-medium', 'font-semibold', 'font-bold', 'font-inter', 'font-bebas',
    'text-sm', 'text-base', 'text-lg', 'text-xl',
    
    // Transitions
    'transition-colors', 'duration-200', 'ease-in-out',
  ]
}