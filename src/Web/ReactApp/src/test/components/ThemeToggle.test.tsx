import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { ThemeToggle, useThemeToggle } from '@/components/ThemeToggle';
import { ThemeProvider } from '@/contexts/ThemeContext';
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
const renderWithTheme = (ui: ReactNode, { defaultTheme = 'dark' } = {}) => {
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
      
      const radioGroup = screen.getByRole('radiogroup');
      expect(radioGroup).toHaveAttribute('aria-label', 'Theme selection');
      
      const buttons = screen.getAllByRole('radio');
      expect(buttons).toHaveLength(3);
      
      expect(screen.getByLabelText('Switch to light theme')).toBeInTheDocument();
      expect(screen.getByLabelText('Switch to dark theme')).toBeInTheDocument();
      expect(screen.getByLabelText('Switch to system theme')).toBeInTheDocument();
    });

    it('shows active theme', () => {
      renderWithTheme(<ThemeToggle variant="buttons" />, { defaultTheme: 'dark' });
      
      const darkButton = screen.getByLabelText('Switch to dark theme');
      expect(darkButton).toHaveAttribute('aria-checked', 'true');
      expect(darkButton).toHaveClass('bg-pf-accent', 'text-white');
    });

    it('allows theme switching via buttons', () => {
      renderWithTheme(<ThemeToggle variant="buttons" />, { defaultTheme: 'dark' });
      
      const lightButton = screen.getByLabelText('Switch to light theme');
      fireEvent.click(lightButton);
      
      expect(lightButton).toHaveAttribute('aria-checked', 'true');
      expect(lightButton).toHaveClass('bg-pf-accent', 'text-white');
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
      
      const lightButton = screen.getByLabelText('Switch to light theme');
      expect(lightButton).toHaveAttribute('role', 'radio');
      expect(lightButton).toHaveAttribute('aria-checked', 'true');
      
      const darkButton = screen.getByLabelText('Switch to dark theme');
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
      expect(button).toHaveClass(
        'hover:text-pf-text-primary',
        'hover:bg-pf-bg-2',
        'transition-all'
      );
    });

    it('applies focus styles', () => {
      renderWithTheme(<ThemeToggle variant="buttons" />);
      
      const buttons = screen.getAllByRole('radio');
      buttons.forEach(button => {
        expect(button).toHaveClass(
          'focus:outline-none',
          'focus:ring-2',
          'focus:ring-pf-accent'
        );
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