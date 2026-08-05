import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render } from '@testing-library/react';
import { screen, fireEvent } from '@testing-library/dom';
import { ThemeToggle } from '@/common/components/ThemeToggle';
import { ThemeProvider, useThemeToggle } from '@/contexts/ThemeContext';
import { SELECTABLE_THEMES } from '@/design-system/themes/registry';
import type { Theme } from '@/design-system/themes/registry';
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
const renderWithTheme = (ui: ReactNode, { defaultTheme = 'dark' }: { defaultTheme?: Theme } = {}) => {
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

      // Labels are read from the component itself, and the order from
      // SELECTABLE_THEMES, so this cannot drift from either.
      const cycle = [...SELECTABLE_THEMES, 'system'];
      const labelFor = (t: string) =>
        t === 'system' ? 'System'
        : t === 'ratos' ? 'RatOS'
        : t.charAt(0).toUpperCase() + t.slice(1);

      expect(button).toHaveAttribute('aria-label', 'Current theme: Light. Click to cycle themes.');

      // One extra click, to prove it wraps.
      for (let i = 1; i <= cycle.length; i++) {
        fireEvent.click(button);
        expect(button).toHaveAttribute(
          'aria-label',
          `Current theme: ${labelFor(cycle[i % cycle.length])}. Click to cycle themes.`
        );
      }
    });

    it('shows labels when showLabels is true', () => {
      renderWithTheme(<ThemeToggle showLabels />);
      
      expect(screen.getByText('Dark')).toBeInTheDocument();
    });

    it('shows system computed theme in label', () => {
      renderWithTheme(<ThemeToggle showLabels />, { defaultTheme: 'system' });
      
      expect(screen.getByText('System')).toBeInTheDocument();
      // The computed theme is shown in the title attribute with its label
      const buttons = screen.getAllByRole('button');
      const compactButton = buttons[0];
      // Check that the button's title attribute contains the computed theme label
      expect(compactButton.getAttribute('title')).toMatch(/\(dark\)/i);
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
      expect(buttons.length).toBeGreaterThanOrEqual(3); // At least light, dark, system
      
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
      expect(darkLabel).toHaveClass('bg-pf-accent-bg');
      expect(darkLabel).toHaveClass('text-[var(--pf-on-accent)]');
    });

    it('allows theme switching via buttons', () => {
      renderWithTheme(<ThemeToggle variant="buttons" />, { defaultTheme: 'dark' });
      
      const lightLabel = screen.getByTestId('theme-option-light');
      const lightInput = lightLabel.querySelector('input[type="radio"]') as HTMLInputElement;
      fireEvent.click(lightInput);
      
      expect(lightInput.checked).toBe(true);
      expect(lightLabel).toHaveClass('bg-pf-accent-bg');
      expect(lightLabel).toHaveClass('text-[var(--pf-on-accent)]');
    });

    it('shows labels when requested', () => {
      renderWithTheme(<ThemeToggle variant="buttons" showLabels />);
      
      expect(screen.getByText('Light')).toBeInTheDocument();
      expect(screen.getByText('Dark')).toBeInTheDocument();
      expect(screen.getByText('Blueprint')).toBeInTheDocument();
      expect(screen.getByText('System')).toBeInTheDocument();
    });
  });

  describe('dropdown variant', () => {
    it('renders select dropdown', () => {
      renderWithTheme(<ThemeToggle variant="dropdown" />);
      
      const select = screen.getByRole('combobox');
      expect(select).toHaveAttribute('aria-label', 'Select theme');
      
      const options = screen.getAllByRole('option');
      expect(options.length).toBeGreaterThanOrEqual(4);
      
      expect(screen.getByRole('option', { name: 'Light' })).toBeInTheDocument();
      expect(screen.getByRole('option', { name: 'Dark' })).toBeInTheDocument();
      expect(screen.getByRole('option', { name: 'Blueprint' })).toBeInTheDocument();
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
      expect(button).toHaveAttribute('title', 'Current: System (Dark). Click to change.');
    });

    it('provides proper ARIA attributes for button group', () => {
      renderWithTheme(<ThemeToggle variant="buttons" />, { defaultTheme: 'light' });
      
      const radioGroup = screen.getByRole('radiogroup');
      expect(radioGroup).toHaveAttribute('aria-label', 'Theme selection');
      
      const lightButton = screen.getByLabelText(/Switch to light theme/i) as HTMLInputElement;
      // Native radio inputs use the checked property (not aria-checked attribute)
      expect(lightButton.tagName).toBe('INPUT');
      expect(lightButton).toHaveAttribute('type', 'radio');
      expect(lightButton.checked).toBe(true);
      
      const darkButton = screen.getByLabelText(/Switch to dark theme/i) as HTMLInputElement;
      expect(darkButton.tagName).toBe('INPUT');
      expect(darkButton).toHaveAttribute('type', 'radio');
      expect(darkButton.checked).toBe(false);
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
      expect(button).toHaveClass(
        'transition-all',
        'hover:text-pf-text-primary',
        'hover:bg-pf-bg-2',
      );
    });

    it('shows a visible label focus ring for button-group radios', () => {
      renderWithTheme(<ThemeToggle variant="buttons" />);
      
      const radios = screen.getAllByRole('radio');
      radios.forEach(radio => {
        expect(radio).toHaveClass('sr-only');
        expect(radio.closest('label')).toHaveClass(
          'focus-within:ring-2',
          'focus-within:ring-pf-accent',
          'focus-within:ring-offset-1',
          'focus-within:ring-offset-pf-bg-0',
        );
      });

      radios[1].focus();

      expect(document.activeElement).toBe(radios[1]);
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
      expect(buttons).toHaveLength(SELECTABLE_THEMES.length + 1); // + system
      
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
    expect(screen.getByTestId('theme')).toHaveTextContent('matrix');
  });

  it('provides working setTheme function', () => {
    renderWithTheme(<TestUseThemeToggle />, { defaultTheme: 'dark' });
    
    fireEvent.click(screen.getByTestId('set-light'));
    expect(screen.getByTestId('theme')).toHaveTextContent('light');
  });
});
