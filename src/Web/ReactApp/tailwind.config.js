/** @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    "./index.html",
    "./src/**/*.{js,ts,jsx,tsx}",
    "./src/**/**/*.{js,ts,jsx,tsx}",
    // Ensure all component files are scanned
    "./src/components/**/*.{js,ts,jsx,tsx}",
    "./src/pages/**/*.{js,ts,jsx,tsx}",
    "./src/contexts/**/*.{js,ts,jsx,tsx}",
    "./src/hooks/**/*.{js,ts,jsx,tsx}",
  ],
  // Tailwind 3.x safelist with explicit class names used in components
  safelist: [
    // Background colors used in components
    'bg-white', 'bg-black',
    'bg-gray-50', 'bg-gray-100', 'bg-gray-200', 'bg-gray-300', 'bg-gray-600', 'bg-gray-700',
    'bg-blue-50', 'bg-blue-100', 'bg-blue-500', 'bg-blue-600', 'bg-blue-700',
    'bg-green-50', 'bg-green-100', 'bg-green-600',
    'bg-red-50', 'bg-red-100', 'bg-red-500',
    'bg-yellow-50', 'bg-yellow-100',
    
    // Text colors used in components
    'text-white', 'text-black',
    'text-gray-400', 'text-gray-500', 'text-gray-600', 'text-gray-700', 'text-gray-800', 'text-gray-900',
    'text-blue-500', 'text-blue-600', 'text-blue-700', 'text-blue-800',
    'text-green-600', 'text-green-800',
    'text-red-500', 'text-red-600', 'text-red-700', 'text-red-800',
    'text-yellow-600', 'text-yellow-800',
    
    // Border colors used in components
    'border-gray-200', 'border-gray-300',
    'border-blue-200', 'border-blue-300',
    'border-green-300', 'border-red-300', 'border-yellow-300',
    
    // Hover states
    'hover:bg-gray-200', 'hover:bg-gray-300', 'hover:bg-gray-700', 'hover:bg-blue-700',
    'hover:text-gray-700', 'hover:text-blue-700', 'hover:text-red-700',
    'hover:shadow-md',
    
    // Focus states
    'focus:ring-blue-500', 'focus:border-blue-500', 'focus:ring-2', 'focus:ring-offset-2', 'focus:outline-none', 'focus:ring-inset',
    
    // Disabled states
    'disabled:bg-gray-300', 'disabled:bg-gray-50', 'disabled:cursor-not-allowed', 'disabled:opacity-50',
    
    // Common utilities
    'shadow', 'shadow-sm', 'shadow-md', 'shadow-lg', 'shadow-xl', 'shadow-2xl',
    'rounded', 'rounded-sm', 'rounded-md', 'rounded-lg', 'rounded-xl', 'rounded-2xl', 'rounded-full',
    'border', 'border-2', 'border-t', 'border-r', 'border-b', 'border-l',
    'p-1', 'p-2', 'p-3', 'p-4', 'p-5', 'p-6', 'p-8',
    'px-1', 'px-2', 'px-3', 'px-4', 'px-5', 'px-6', 'px-8',
    'py-1', 'py-2', 'py-3', 'py-4', 'py-5', 'py-6', 'py-8',
    'm-1', 'm-2', 'm-3', 'm-4', 'm-5', 'm-6', 'm-8',
    'mx-1', 'mx-2', 'mx-3', 'mx-4', 'mx-5', 'mx-6', 'mx-8', 'mx-auto',
    'my-1', 'my-2', 'my-3', 'my-4', 'my-5', 'my-6', 'my-8',
    'gap-1', 'gap-2', 'gap-3', 'gap-4', 'gap-5', 'gap-6', 'gap-8',
    'space-x-1', 'space-x-2', 'space-x-3', 'space-x-4', 'space-x-5', 'space-x-6', 'space-x-8',
    'space-y-1', 'space-y-2', 'space-y-3', 'space-y-4', 'space-y-5', 'space-y-6', 'space-y-8',
  ],
  theme: {
    extend: {},
  },
  plugins: [
    require('@tailwindcss/forms'),
  ],
}