import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render } from '@testing-library/react';
import { screen } from '@testing-library/dom';
import { act } from '@testing-library/react';
import { ThemeProvider, useTheme, useComputedTheme, useAccessibilityPreferences, SELECTABLE_THEMES } from '@/contexts/ThemeContext';
import type { Theme } from '@/contexts/ThemeContext';
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

// Test component to access theme context
function TestComponent() {
  const { theme, computedTheme, setTheme, toggleTheme, prefersReducedMotion, prefersHighContrast } = useTheme();
  
  return (
    <div>
      <div data-testid="theme">{theme}</div>
      <div data-testid="computed-theme">{computedTheme}</div>
      <div data-testid="reduced-motion">{prefersReducedMotion ? 'true' : 'false'}</div>
      <div data-testid="high-contrast">{prefersHighContrast ? 'true' : 'false'}</div>
      <button data-testid="set-light" onClick={() => setTheme('light')}>Set Light</button>
      <button data-testid="set-dark" onClick={() => setTheme('dark')}>Set Dark</button>
      <button data-testid="set-system" onClick={() => setTheme('system')}>Set System</button>
      <button data-testid="toggle-theme" onClick={toggleTheme}>Toggle</button>
    </div>
  );
}

function TestHooks() {
  const computedTheme = useComputedTheme();
  const { prefersReducedMotion, prefersHighContrast } = useAccessibilityPreferences();
  
  return (
    <div>
      <div data-testid="hook-computed-theme">{computedTheme}</div>
      <div data-testid="hook-reduced-motion">{prefersReducedMotion ? 'true' : 'false'}</div>
      <div data-testid="hook-high-contrast">{prefersHighContrast ? 'true' : 'false'}</div>
    </div>
  );
}

type ThemeType = Theme;
const renderWithThemeProvider = (
  ui: ReactNode, 
  { defaultTheme = 'dark' as ThemeType, storageKey = 'test-theme' }: { defaultTheme?: ThemeType, storageKey?: string } = {}
) => {
  return render(
    <ThemeProvider defaultTheme={defaultTheme} storageKey={storageKey}>
      {ui}
    </ThemeProvider>
  );
};

describe('ThemeContext', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorageMock.getItem.mockReturnValue(null);
    
    // Mock all matchMedia queries (prefers-color-scheme, prefers-reduced-motion, prefers-contrast)
    window.matchMedia = vi.fn().mockImplementation((query) => {
      if (query.includes('prefers-color-scheme')) {
        return { matches: true, addEventListener: vi.fn(), removeEventListener: vi.fn() };
      }
      if (query.includes('prefers-reduced-motion')) {
        return { matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() };
      }
      if (query.includes('prefers-contrast')) {
        return { matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() };
      }
      return { matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() };
    });
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  describe('ThemeProvider', () => {
    it('provides default theme values', () => {
      renderWithThemeProvider(<TestComponent />);
      
      expect(screen.getByTestId('theme')).toHaveTextContent('dark');
      expect(screen.getByTestId('computed-theme')).toHaveTextContent('dark');
      expect(screen.getByTestId('reduced-motion')).toHaveTextContent('false');
      expect(screen.getByTestId('high-contrast')).toHaveTextContent('false');
    });

    it('uses custom default theme', () => {
      renderWithThemeProvider(<TestComponent />, { defaultTheme: 'light' });
      
      expect(screen.getByTestId('theme')).toHaveTextContent('light');
      expect(screen.getByTestId('computed-theme')).toHaveTextContent('light');
    });

    it('loads theme from localStorage on mount', () => {
      localStorageMock.getItem.mockReturnValue('light');
      
      renderWithThemeProvider(<TestComponent />);
      
      expect(localStorageMock.getItem).toHaveBeenCalledWith('test-theme');
      expect(screen.getByTestId('theme')).toHaveTextContent('light');
    });

    it('falls back to the default theme for unrecognised localStorage values', () => {
      // Previously this test asserted the opposite of its own name: the value
      // was loaded verbatim and applied as a data-theme attribute matching no
      // stylesheet. ThemeContext now validates on read.
      localStorageMock.getItem.mockReturnValue('invalid-theme');

      renderWithThemeProvider(<TestComponent />);

      expect(screen.getByTestId('theme')).toHaveTextContent('dark');
    });

    it('migrates a retired theme to dark', () => {
      // forge/github-dark/printfarmer-dark rendered as dark anyway — their
      // stylesheets were in layer(base) and lost the cascade to the unlayered
      // design-system themes — so this preserves what users actually saw.
      localStorageMock.getItem.mockReturnValue('forge');

      renderWithThemeProvider(<TestComponent />);

      expect(screen.getByTestId('theme')).toHaveTextContent('dark');
    });

    it('sets custom storage key', () => {
      localStorageMock.getItem.mockReturnValue('system');
      
      renderWithThemeProvider(<TestComponent />, { storageKey: 'custom-key' });
      
      expect(localStorageMock.getItem).toHaveBeenCalledWith('custom-key');
    });
  });

  describe('theme switching', () => {
    it('allows setting light theme', async () => {
      renderWithThemeProvider(<TestComponent />);
      
      await act(async () => {
        screen.getByTestId('set-light').click();
      });
      
      expect(screen.getByTestId('theme')).toHaveTextContent('light');
      expect(screen.getByTestId('computed-theme')).toHaveTextContent('light');
      expect(localStorageMock.setItem).toHaveBeenCalledWith('test-theme', 'light');
    });

    it('allows setting dark theme', async () => {
      renderWithThemeProvider(<TestComponent />, { defaultTheme: 'light' });
      
      await act(async () => {
        screen.getByTestId('set-dark').click();
      });
      
      expect(screen.getByTestId('theme')).toHaveTextContent('dark');
      expect(screen.getByTestId('computed-theme')).toHaveTextContent('dark');
      expect(localStorageMock.setItem).toHaveBeenCalledWith('test-theme', 'dark');
    });

    it('allows setting system theme', async () => {
      renderWithThemeProvider(<TestComponent />);
      
      await act(async () => {
        screen.getByTestId('set-system').click();
      });
      
      expect(screen.getByTestId('theme')).toHaveTextContent('system');
      expect(screen.getByTestId('computed-theme')).toHaveTextContent('dark'); // matchMedia mocked to dark
      expect(localStorageMock.setItem).toHaveBeenCalledWith('test-theme', 'system');
    });
  });

  describe('toggleTheme', () => {
    // Derived from SELECTABLE_THEMES rather than hardcoded. The previous
    // version enumerated a cycle by hand and kept asserting a rotation through
    // three themes that no longer existed.
    const cycle: Theme[] = [...SELECTABLE_THEMES, 'system'];

    it(`cycles through every selectable theme then system: ${cycle.join(' -> ')} -> ${cycle[0]}`, async () => {
      renderWithThemeProvider(<TestComponent />, { defaultTheme: cycle[0] });

      expect(screen.getByTestId('theme').textContent).toBe(cycle[0]);

      // One extra step, to prove it wraps rather than stopping at the end.
      for (let i = 1; i <= cycle.length; i++) {
        await act(async () => {
          screen.getByTestId('toggle-theme').click();
        });
        expect(screen.getByTestId('theme').textContent).toBe(cycle[i % cycle.length]);
      }
    });

    it.each(cycle.map((from, i) => [from, cycle[(i + 1) % cycle.length]]))(
      'toggles from %s to %s',
      async (from, to) => {
        renderWithThemeProvider(<TestComponent />, { defaultTheme: from });

        await act(async () => {
          screen.getByTestId('toggle-theme').click();
        });
        expect(screen.getByTestId('theme').textContent).toBe(to);
      }
    );
  });

  describe('system theme detection', () => {
    it('detects system dark theme preference as dark', () => {
      window.matchMedia = createMockMatchMedia(true);
      
      renderWithThemeProvider(<TestComponent />, { defaultTheme: 'system' });
      
      expect(screen.getByTestId('computed-theme')).toHaveTextContent('dark');
    });

    it('detects system light theme preference as light', () => {
      window.matchMedia = createMockMatchMedia(false);
      
      renderWithThemeProvider(<TestComponent />, { defaultTheme: 'system' });
      
      expect(screen.getByTestId('computed-theme')).toHaveTextContent('light');
    });

    it('responds to system theme changes', async () => {
      const mockMatchMedia = vi.fn().mockImplementation((query) => {
        const mediaQueryList = {
          matches: true, // Initially dark
          media: query,
          onchange: null,
          addEventListener: vi.fn(),
          removeEventListener: vi.fn(),
          dispatchEvent: vi.fn(),
        };
        
        return mediaQueryList;
      });
      window.matchMedia = mockMatchMedia;
      
      renderWithThemeProvider(<TestComponent />, { defaultTheme: 'system' });
      
      expect(screen.getByTestId('computed-theme')).toHaveTextContent('dark');
      
      // Simulate system theme change
      const mediaQueryList = mockMatchMedia.mock.results[0].value;
      const changeHandler = mediaQueryList.addEventListener.mock.calls.find(
        (call: [string, EventListenerOrEventListenerObject]) => call[0] === 'change'
      )?.[1];
      
      if (changeHandler) {
        await act(async () => {
          changeHandler({ matches: false }); // Change to light
        });
        
        expect(screen.getByTestId('computed-theme')).toHaveTextContent('light');
      }
    });
  });

  describe('accessibility preferences', () => {
    it('detects reduced motion preference', () => {
      // Use query-based mock to handle multiple matchMedia calls in any order
      const mockMatchMedia = vi.fn().mockImplementation((query) => {
        const results: Record<string, boolean> = {
          '(prefers-color-scheme: dark)': true,
          '(prefers-reduced-motion: reduce)': true,
          '(prefers-contrast: more)': false,
        };
        const matches = results[query] ?? false;
        return {
          matches,
          media: query,
          addEventListener: vi.fn(),
          removeEventListener: vi.fn(),
          dispatchEvent: vi.fn(),
        };
      });
      
      window.matchMedia = mockMatchMedia;
      
      renderWithThemeProvider(<TestComponent />);
      
      expect(screen.getByTestId('reduced-motion')).toHaveTextContent('true');
    });

    it('detects high contrast preference', () => {
      // Create a custom mock that returns different values based on the query string
      const mockMatchMedia = vi.fn().mockImplementation((query) => {
        const results: Record<string, boolean> = {
          '(prefers-color-scheme: dark)': true,
          '(prefers-reduced-motion: reduce)': false,
          '(prefers-contrast: more)': true,
        };
        const matches = results[query] ?? false;
        return {
          matches,
          media: query,
          addEventListener: vi.fn(),
          removeEventListener: vi.fn(),
          dispatchEvent: vi.fn(),
        };
      });
      
      window.matchMedia = mockMatchMedia;
      
      renderWithThemeProvider(<TestComponent />);
      
      expect(screen.getByTestId('high-contrast')).toHaveTextContent('true');
    });
  });

  describe('DOM manipulation', () => {
    it('sets data-theme attribute for light theme', async () => {
      renderWithThemeProvider(<TestComponent />);
      
      await act(async () => {
        screen.getByTestId('set-light').click();
      });
      
      expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    });

    it('sets data-theme explicitly for every theme, including dark', async () => {
      // Dark used to be signalled by *removing* the attribute, a leftover from
      // when github-dark was the bare-:root default. index.html now always sets
      // data-theme before first paint, so an implicit default can never apply.
      renderWithThemeProvider(<TestComponent />, { defaultTheme: 'light' });

      expect(document.documentElement.getAttribute('data-theme')).toBe('light');

      await act(async () => {
        screen.getByTestId('set-dark').click();
      });

      expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    });

    it('applies reduced motion CSS variable', () => {
      // Use query-based mock to handle multiple matchMedia calls in any order
      const mockMatchMedia = vi.fn().mockImplementation((query) => {
        const results: Record<string, boolean> = {
          '(prefers-color-scheme: dark)': true,
          '(prefers-reduced-motion: reduce)': true,
          '(prefers-contrast: more)': false,
        };
        const matches = results[query] ?? false;
        return {
          matches,
          media: query,
          addEventListener: vi.fn(),
          removeEventListener: vi.fn(),
          dispatchEvent: vi.fn(),
        };
      });
      
      window.matchMedia = mockMatchMedia;
      
      renderWithThemeProvider(<TestComponent />);
      
      // Note: In real implementation, this would set CSS variable
      // We can't easily test CSS variable setting in jsdom
      expect(screen.getByTestId('reduced-motion')).toHaveTextContent('true');
    });
  });

  describe('custom events', () => {
    it('dispatches theme change event', async () => {
      const eventListener = vi.fn();
      window.addEventListener('themeChange', eventListener);
      
      renderWithThemeProvider(<TestComponent />);
      
      await act(async () => {
        screen.getByTestId('set-light').click();
      });
      
      expect(eventListener).toHaveBeenCalled();
      const lastCall = eventListener.mock.calls[eventListener.mock.calls.length - 1][0];
      expect(lastCall.type).toBe('themeChange');
      expect(lastCall.detail).toMatchObject({
        theme: 'light',
        computedTheme: 'light',
        prefersReducedMotion: expect.any(Boolean),
        prefersHighContrast: expect.any(Boolean),
      });
      
      window.removeEventListener('themeChange', eventListener);
    });
  });

  describe('error handling', () => {
    it('throws error when useTheme is used outside provider', () => {
      const TestComponentWithoutProvider = () => {
        useTheme();
        return null;
      };
      
      expect(() => render(<TestComponentWithoutProvider />)).toThrow(
        'useTheme must be used within a ThemeProvider'
      );
    });
  });

  describe('hook utilities', () => {
    it('useComputedTheme returns computed theme', () => {
      renderWithThemeProvider(<TestHooks />, { defaultTheme: 'system' });
      
      expect(screen.getByTestId('hook-computed-theme')).toHaveTextContent('dark');
    });

    it('useAccessibilityPreferences returns preferences', () => {
      // Use query-based mock to handle multiple matchMedia calls in any order
      const mockMatchMedia = vi.fn().mockImplementation((query) => {
        const results: Record<string, boolean> = {
          '(prefers-color-scheme: dark)': true,
          '(prefers-reduced-motion: reduce)': true,
          '(prefers-contrast: more)': true,
        };
        const matches = results[query] ?? false;
        return {
          matches,
          media: query,
          addEventListener: vi.fn(),
          removeEventListener: vi.fn(),
          dispatchEvent: vi.fn(),
        };
      });
      
      window.matchMedia = mockMatchMedia;
      
      renderWithThemeProvider(<TestHooks />);
      
      expect(screen.getByTestId('hook-reduced-motion')).toHaveTextContent('true');
      expect(screen.getByTestId('hook-high-contrast')).toHaveTextContent('true');
    });
  });
});
