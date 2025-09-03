/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    "./src/**/*.{js,jsx,ts,tsx}",
    "./index.html",
  ],
  theme: {
    extend: {
      colors: {
        // PrintFarmer Dark Theme Colors - matching original Blazor
        'pf': {
          // Primary backgrounds (from CSS variables)
          'bg-0': '#0b1020',    // --bg-0: main background
          'bg-1': '#0f172a',    // --bg-1: secondary background  
          'bg-2': '#111827',    // --bg-2: tertiary background
          'panel': '#0e1528',   // --panel: panel background
          
          // Borders and dividers
          'border': '#243145',  // --border: main border color
          'border-light': '#475569',
          'border-medium': '#334155',
          'border-dark': '#1f2937',
          
          // Text colors
          'text-primary': '#e5e7eb',    // --color-text-primary
          'text-secondary': '#9ca3af',  // --color-text-secondary  
          'text-tertiary': '#6b7280',   // --color-text-tertiary
          'text-light': '#cbd5e1',
          'text-muted': '#94a3b8',
          
          // Accent colors
          'accent': '#10b981',          // --accent: primary green
          'accent-2': '#2563eb',        // --accent-2: blue accent
          'success': '#10b981',         // --color-success
          'success-hover': '#059669',   // --color-primary-hover
          'link': '#93c5fd',           // --color-link
          
          // Status colors
          'status-online-bg': '#064e3b',
          'status-online-text': '#d1fae5', 
          'status-online-border': '#065f46',
          'status-offline-bg': '#450a0a',
          'status-offline-text': '#fee2e2',
          'status-offline-border': '#7f1d1d',
          
          // Error and warning
          'error': '#ef4444',
          'error-bg': '#450a0a',
          'error-text': '#fee2e2',
          'error-border': '#7f1d1d',
          'warning': '#f59e0b',
          'warning-text': '#fecaca',
          
          // Loading and disabled
          'loading': '#93c5fd',
          'loading-border': '#2563eb',
          'disabled': '#9ca3af',
          
          // Gradient colors for buttons
          'gradient': {
            'primary-start': '#172036',
            'primary-end': '#0f172a',
            'secondary-start': '#1a2542',
            'secondary-end': '#111b30',
            'success-start': '#34d399',
            'success-end': '#10b981',
            'green-start': '#22c55e',
            'green-end': '#16a34a',
            'green-active-start': '#16a34a',
            'green-active-end': '#15803d',
            'gray-start': '#64748b',
            'gray-end': '#475569',
            'gray-dark-start': '#4a5668',
            'gray-dark-end': '#334155',
          }
        }
      },
      fontFamily: {
        'inter': ['Inter', 'ui-sans-serif', 'system-ui'],
        'bebas': ['Bebas Neue', 'sans-serif'],
      },
    },
  },
  plugins: [],
  safelist: [
    // PrintFarmer specific colors
    'bg-pf-bg-0', 'bg-pf-bg-1', 'bg-pf-bg-2', 'bg-pf-panel',
    'text-pf-text-primary', 'text-pf-text-secondary', 'text-pf-text-tertiary',
    'text-pf-text-light', 'text-pf-text-muted',
    'border-pf-border', 'border-pf-border-light', 'border-pf-border-medium',
    'bg-pf-accent', 'bg-pf-success', 'text-pf-accent', 'text-pf-success',
    'bg-pf-status-online-bg', 'text-pf-status-online-text', 'border-pf-status-online-border',
    'bg-pf-status-offline-bg', 'text-pf-status-offline-text', 'border-pf-status-offline-border',
    'bg-pf-error', 'text-pf-error-text', 'border-pf-error-border',
    'text-pf-link', 'bg-pf-loading', 'text-pf-loading',
    
    // Hover states for PrintFarmer colors
    'hover:bg-pf-bg-1', 'hover:bg-pf-bg-2', 'hover:bg-pf-success-hover',
    'hover:text-pf-text-primary', 'hover:text-pf-accent', 'hover:border-pf-accent-2',
    
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
    'justify-between', 'gap-2', 'gap-3', 'gap-4',
    
    // Font weights and sizes
    'font-medium', 'font-semibold', 'font-bold', 'font-inter', 'font-bebas',
    'text-sm', 'text-base', 'text-lg', 'text-xl',
    
    // Transitions
    'transition-colors', 'duration-200', 'ease-in-out',
  ]
}