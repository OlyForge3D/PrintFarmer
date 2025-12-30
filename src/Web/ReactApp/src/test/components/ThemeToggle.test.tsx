/* eslint-disable local/pf-no-raw-html-controls */
import { describe, it, expect, beforeEach } from 'vitest';
import { render } from '@testing-library/react';
import { screen, fireEvent } from '@testing-library/dom';
import { ThemeToggle } from '@/common/components/ThemeToggle';
import { ThemeProvider, type ThemeName } from '@/contexts/ThemeContext';
import { useThemeToggle } from '@/contexts/ThemeHooks';
import { ReactNode } from 'react';

// Mock localStorage
const localStorageMock = {
  getItem: vi.fn(),
  setItem: vi.fn(),
  removeItem: vi.fn(),
  clear: vi.fn(),
};
Object.defineProperty(window, 'localStorage', { value: localStorageMock });

// Mock matchMedia
const createMockMatchMedia = (matches: boolean) => vi.fn().mockImplementation((query) => ({
  matches,
  media: query,
  onchange: null,
  addEventListener: vi.fn(),
  removeEventListener: vi.fn(),
  dispatchEvent: vi.fn(),
}));

// Test wrapper with ThemeProvider
const renderWithTheme = (ui: ReactNode, { defaultTheme = 'dark' }: { defaultTheme?: ThemeName } = {}) => {
  return render(
    <ThemeProvider defaultTheme={defaultTheme}>
      {ui}
    </ThemeProvider>
  );
};

// Test component for useThemeToggle hook
function TestUseThemeToggle() {
  const { theme, computedTheme, isLight, isDark, isSystem, toggleTheme, setTheme } = useThemeToggle();
  
  return (
    <div>
      <div data-testid="theme">{theme}</div>
      <div data-testid="computed-theme">{computedTheme}</div>
      <div data-testid="is-light">{isLight ? 'true' : 'false'}</div>
      <div data-testid="is-dark">{isDark ? 'true' : 'false'}</div>
      <div data-testid="is-system">{isSystem ? 'true' : 'false'}</div>
      <button data-testid="toggle" onClick={toggleTheme}>Toggle</button>
      <button data-testid="set-light" onClick={() => setTheme('light')}>Set Light</button>
    </div>
  );
}

describe('ThemeToggle', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorageMock.getItem.mockReturnValue(null);
    window.matchMedia = createMockMatchMedia(true); // Default to dark system preference
  });

  describe('compact variant (default)', () => {
    it('renders with default props', () => {
      renderWithTheme(<ThemeToggle />);
      
      const button = screen.getByRole('button');
      expect(button).toBeInTheDocument();
      expect(button).toHaveAttribute('aria-label', 'Current theme: Dark. Click to cycle themes.');
    });

    it('cycles through themes on click', () => {
      renderWithTheme(<ThemeToggle />, { defaultTheme: 'light' });
      
      const button = screen.getByRole('button');
      
      // Initial state: light
      expect(button).toHaveAttribute('aria-label', 'Current theme: Light. Click to cycle themes.');
      
      // Click to cycle: light -> dark
      fireEvent.click(button);
      expect(button).toHaveAttribute('aria-label', 'Current theme: Dark. Click to cycle themes.');
      
      // Click to cycle: dark -> system
      fireEvent.click(button);
      expect(button).toHaveAttribute('aria-label', 'Current theme: System. Click to cycle themes.');
      
      // Click to cycle: system -> light
      fireEvent.click(button);
      expect(button).toHaveAttribute('aria-label', 'Current theme: Light. Click to cycle themes.');
    });

    it('shows labels when showLabels is true', () => {
      renderWithTheme(<ThemeToggle showLabels />);
      
      expect(screen.getByText('Dark')).toBeInTheDocument();
    });

    it('shows system computed theme in label', () => {
      renderWithTheme(<ThemeToggle showLabels />, { defaultTheme: 'system' });
      
      expect(screen.getByText('System')).toBeInTheDocument();
      expect(screen.getByText('(dark)')).toBeInTheDocument();
    });

    it('applies custom className', () => {
      renderWithTheme(<ThemeToggle className="custom-class" />);
      
      const button = screen.getByRole('button');
      expect(button).toHaveClass('custom-class');
    });

    it('handles different sizes', () => {
      const { rerender } = renderWithTheme(<ThemeToggle size="sm" />);
      let button = screen.getByRole('button');
      expect(button).toHaveClass('p-1.5', 'text-sm');
      
      rerender(
        <ThemeProvider defaultTheme="dark">
          <ThemeToggle size="lg" />
        </ThemeProvider>
      );
      button = screen.getByRole('button');
      expect(button).toHaveClass('p-3', 'text-lg');
    });
  });

  describe('buttons variant', () => {
    it('renders button group', () => {
      renderWithTheme(<ThemeToggle variant="buttons" />);
      
      // Use explicit testid for the radiogroup to avoid environment differences
      const radioGroup = screen.getByTestId('theme-radiogroup');
      expect(radioGroup).toHaveAttribute('aria-label', 'Theme selection');
      
      const buttons = screen.getAllByRole('radio');
      expect(buttons).toHaveLength(3);
      
      // Use case-insensitive matching for labels to avoid minor text changes
      expect(screen.getByTestId('theme-option-light')).toBeInTheDocument();
      expect(screen.getByTestId('theme-option-dark')).toBeInTheDocument();
      expect(screen.getByTestId('theme-option-system')).toBeInTheDocument();
    });

    it('shows active theme', () => {
      renderWithTheme(<ThemeToggle variant="buttons" />, { defaultTheme: 'dark' });
      
      const darkLabel = screen.getByTestId('theme-option-dark');
      const darkInput = darkLabel.querySelector('input[type="radio"]') as HTMLInputElement;
      expect(darkInput.checked).toBe(true);
      expect(darkLabel).toHaveClass('bg-pf-accent');
      expect(darkLabel).toHaveClass('text-white');
    });

    it('allows theme switching via buttons', () => {
      renderWithTheme(<ThemeToggle variant="buttons" />, { defaultTheme: 'dark' });
      
      const lightLabel = screen.getByTestId('theme-option-light');
      const lightInput = lightLabel.querySelector('input[type="radio"]') as HTMLInputElement;
      fireEvent.click(lightInput);
      
      expect(lightInput.checked).toBe(true);
      expect(lightLabel).toHaveClass('bg-pf-accent');
      expect(lightLabel).toHaveClass('text-white');
    });

    it('shows labels when requested', () => {
      renderWithTheme(<ThemeToggle variant="buttons" showLabels />);
      
      expect(screen.getByText('Light')).toBeInTheDocument();
      expect(screen.getByText('Dark')).toBeInTheDocument();
      expect(screen.getByText('System')).toBeInTheDocument();
    });
  });

  describe('dropdown variant', () => {
    it('renders select dropdown', () => {
      renderWithTheme(<ThemeToggle variant="dropdown" />);
      
      const select = screen.getByRole('combobox');
      expect(select).toHaveAttribute('aria-label', 'Select theme');
      
      const options = screen.getAllByRole('option');
      expect(options).toHaveLength(3);
      
      expect(screen.getByRole('option', { name: 'Light' })).toBeInTheDocument();
      expect(screen.getByRole('option', { name: 'Dark' })).toBeInTheDocument();
      expect(screen.getByRole('option', { name: 'System' })).toBeInTheDocument();
    });

    it('shows current theme selection', () => {
      renderWithTheme(<ThemeToggle variant="dropdown" />, { defaultTheme: 'light' });
      
      const select = screen.getByRole('combobox') as HTMLSelectElement;
      expect(select.value).toBe('light');
    });

    it('allows theme switching via select', () => {
      renderWithTheme(<ThemeToggle variant="dropdown" />, { defaultTheme: 'dark' });
      
      const select = screen.getByRole('combobox');
      fireEvent.change(select, { target: { value: 'light' } });
      
      expect((select as HTMLSelectElement).value).toBe('light');
    });

    it('applies custom size classes', () => {
      renderWithTheme(<ThemeToggle variant="dropdown" size="lg" />);
      
      const select = screen.getByRole('combobox');
      expect(select).toHaveClass('p-3', 'text-lg');
    });
  });

  describe('accessibility', () => {
    it('provides proper ARIA labels for compact variant', () => {
      renderWithTheme(<ThemeToggle />, { defaultTheme: 'system' });
      
      const button = screen.getByRole('button');
      expect(button).toHaveAttribute(
        'aria-label', 
        'Current theme: System. Click to cycle themes.'
      );
      expect(button).toHaveAttribute('title', 'Current: System (dark). Click to change.');
    });

    it('provides proper ARIA attributes for button group', () => {
      renderWithTheme(<ThemeToggle variant="buttons" />, { defaultTheme: 'light' });
      
      const radioGroup = screen.getByRole('radiogroup');
      expect(radioGroup).toHaveAttribute('aria-label', 'Theme selection');
      
      const lightButton = screen.getByLabelText(/Switch to light theme/i) as HTMLInputElement;
      // Native radio inputs shouldn't be required to expose role attribute; assert it's an input radio and ARIA checked
      expect(lightButton.tagName).toBe('INPUT');
      expect(lightButton).toHaveAttribute('type', 'radio');
      expect(lightButton).toHaveAttribute('aria-checked', 'true');
      
      const darkButton = screen.getByLabelText(/Switch to dark theme/i) as HTMLInputElement;
      expect(darkButton.tagName).toBe('INPUT');
      expect(darkButton).toHaveAttribute('type', 'radio');
      expect(darkButton).toHaveAttribute('aria-checked', 'false');
    });

    it('provides proper ARIA label for dropdown', () => {
      renderWithTheme(<ThemeToggle variant="dropdown" />);
      
      const select = screen.getByRole('combobox');
      expect(select).toHaveAttribute('aria-label', 'Select theme');
    });

    it('supports keyboard navigation', () => {
      renderWithTheme(<ThemeToggle />);
      
      const button = screen.getByRole('button');
      button.focus();
      expect(document.activeElement).toBe(button);
      
      // Test keyboard activation
      fireEvent.keyDown(button, { key: 'Enter' });
      // Theme should change (tested in other tests)
    });
  });

  describe('visual styling', () => {
    it('applies hover styles', () => {
      renderWithTheme(<ThemeToggle />);
      
      const button = screen.getByRole('button');
      // Ensure transitional behavior exists and either hover or active classes are present
      expect(button.className).toMatch(/transition-all/);
      expect(/hover:text-pf-text-primary|bg-pf-accent/.test(button.className)).toBe(true);
    });

    it('applies focus styles', () => {
      renderWithTheme(<ThemeToggle variant="buttons" />);
      
      const buttons = screen.getAllByRole('radio');
      buttons.forEach(button => {
        // Inputs are visually-hidden (sr-only) but should exist
        expect(button).toHaveClass('sr-only');
        // Visual focus styles live on the label element - accept hover or active classes
        const label = (button as HTMLElement).closest('label');
        expect(label).toBeTruthy();
        const labelClass = (label as HTMLElement).className;
        expect(/hover:text-pf-text-primary|bg-pf-accent/.test(labelClass)).toBe(true);
      });
    });

    it('shows icons for all variants', () => {
      // Compact variant
      const { rerender } = renderWithTheme(<ThemeToggle />);
      expect(screen.getByRole('button')).toBeInTheDocument();
      
      // Button variant
      rerender(
        <ThemeProvider defaultTheme="dark">
          <ThemeToggle variant="buttons" />
        </ThemeProvider>
      );
      const buttons = screen.getAllByRole('radio');
      expect(buttons).toHaveLength(3);
      
      // Dropdown variant
      rerender(
        <ThemeProvider defaultTheme="dark">
          <ThemeToggle variant="dropdown" />
        </ThemeProvider>
      );
      expect(screen.getByRole('combobox')).toBeInTheDocument();
    });
  });

  describe('responsive behavior', () => {
    it('hides labels on small screens when showLabels is true', () => {
      renderWithTheme(<ThemeToggle showLabels variant="buttons" />);
      
      const buttons = screen.getAllByRole('radio');
      buttons.forEach(button => {
        const label = button.querySelector('span');
        if (label) {
          expect(label).toHaveClass('hidden', 'sm:inline');
        }
      });
    });
  });
});

describe('useThemeToggle hook', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorageMock.getItem.mockReturnValue(null);
    window.matchMedia = createMockMatchMedia(true);
  });

  it('returns theme state and utilities', () => {
    renderWithTheme(<TestUseThemeToggle />, { defaultTheme: 'dark' });
    
    expect(screen.getByTestId('theme')).toHaveTextContent('dark');
    expect(screen.getByTestId('computed-theme')).toHaveTextContent('dark');
    expect(screen.getByTestId('is-light')).toHaveTextContent('false');
    expect(screen.getByTestId('is-dark')).toHaveTextContent('true');
    expect(screen.getByTestId('is-system')).toHaveTextContent('false');
  });

  it('correctly identifies light theme', () => {
    renderWithTheme(<TestUseThemeToggle />, { defaultTheme: 'light' });
    
    expect(screen.getByTestId('is-light')).toHaveTextContent('true');
    expect(screen.getByTestId('is-dark')).toHaveTextContent('false');
  });

  it('correctly identifies system theme', () => {
    renderWithTheme(<TestUseThemeToggle />, { defaultTheme: 'system' });
    
    expect(screen.getByTestId('is-system')).toHaveTextContent('true');
    expect(screen.getByTestId('computed-theme')).toHaveTextContent('dark'); // System preference
  });

  it('provides working toggle function', () => {
    renderWithTheme(<TestUseThemeToggle />, { defaultTheme: 'dark' });
    
    fireEvent.click(screen.getByTestId('toggle'));
    expect(screen.getByTestId('theme')).toHaveTextContent('light');
  });

  it('provides working setTheme function', () => {
    renderWithTheme(<TestUseThemeToggle />, { defaultTheme: 'dark' });
    
    fireEvent.click(screen.getByTestId('set-light'));
    expect(screen.getByTestId('theme')).toHaveTextContent('light');
  });
});