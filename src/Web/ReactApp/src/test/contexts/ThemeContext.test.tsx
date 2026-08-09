import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render } from '@testing-library/react';
import { screen } from '@testing-library/dom';
import { act } from '@testing-library/react';
import { ThemeProvider, useTheme, useComputedTheme, useAccessibilityPreferences } from '@/contexts/ThemeContext';
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

// Test component to access theme context
function TestComponent() {
  const {
    theme, computedTheme, setTheme, toggleTheme, prefersReducedMotion, prefersHighContrast,
    contrastPreference, setContrastPreference, highContrastActive,
  } = useTheme();
  
  return (
    <div>
      <div data-testid="theme">{theme}</div>
      <div data-testid="computed-theme">{computedTheme}</div>
      <div data-testid="reduced-motion">{prefersReducedMotion ? 'true' : 'false'}</div>
      <div data-testid="high-contrast">{prefersHighContrast ? 'true' : 'false'}</div>
      <div data-testid="contrast-preference">{contrastPreference}</div>
      <div data-testid="high-contrast-active">{highContrastActive ? 'true' : 'false'}</div>
      <button data-testid="set-light" onClick={() => setTheme('light')}>Set Light</button>
      <button data-testid="set-dark" onClick={() => setTheme('dark')}>Set Dark</button>
      <button data-testid="set-system" onClick={() => setTheme('system')}>Set System</button>
      <button data-testid="toggle-theme" onClick={toggleTheme}>Toggle</button>
      <button data-testid="set-contrast-high" onClick={() => setContrastPreference('high')}>Set High Contrast</button>
      <button data-testid="set-contrast-normal" onClick={() => setContrastPreference('normal')}>Set Normal Contrast</button>
      <button data-testid="set-contrast-system" onClick={() => setContrastPreference('system')}>Set System Contrast</button>
    </div>
  );
}

function TestHooks() {
  const computedTheme = useComputedTheme();
  const { prefersReducedMotion, prefersHighContrast, contrastPreference, highContrastActive } = useAccessibilityPreferences();
  
  return (
    <div>
      <div data-testid="hook-computed-theme">{computedTheme}</div>
      <div data-testid="hook-reduced-motion">{prefersReducedMotion ? 'true' : 'false'}</div>
      <div data-testid="hook-high-contrast">{prefersHighContrast ? 'true' : 'false'}</div>
      <div data-testid="hook-contrast-preference">{contrastPreference}</div>
      <div data-testid="hook-high-contrast-active">{highContrastActive ? 'true' : 'false'}</div>
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
      // github-dark/printfarmer-dark rendered the dark palette anyway — their
      // stylesheets were in layer(base) and lost the cascade to the unlayered
      // design-system themes — so this preserves what users actually saw, and
      // additionally restores the display font no rule was matching for them.
      localStorageMock.getItem.mockReturnValue('github-dark');

      renderWithThemeProvider(<TestComponent />);

      expect(screen.getByTestId('theme')).toHaveTextContent('dark');
    });

    it('keeps forge, which was migrated rather than retired', () => {
      // forge's colour tokens were inert like the others, but its plain rules
      // (heading and progress-bar glow) faced no competing declaration and did
      // paint. It is a real theme and now has a design-system stylesheet.
      localStorageMock.getItem.mockReturnValue('forge');

      renderWithThemeProvider(<TestComponent />);

      expect(screen.getByTestId('theme')).toHaveTextContent('forge');
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

  describe('high-contrast cascade wiring (#1297)', () => {
    it('sets data-contrast="normal" explicitly with no OS signal and no override', () => {
      renderWithThemeProvider(<TestComponent />);

      expect(document.documentElement.getAttribute('data-contrast')).toBe('normal');
      expect(screen.getByTestId('contrast-preference')).toHaveTextContent('system');
      expect(screen.getByTestId('high-contrast-active')).toHaveTextContent('false');
    });

    it('sets data-contrast="high" when the OS signals prefers-contrast: more', () => {
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

      expect(document.documentElement.getAttribute('data-contrast')).toBe('high');
      expect(screen.getByTestId('high-contrast-active')).toHaveTextContent('true');
    });

    it('manual override forces data-contrast="high" even without the OS signal', async () => {
      renderWithThemeProvider(<TestComponent />);

      expect(document.documentElement.getAttribute('data-contrast')).toBe('normal');

      await act(async () => {
        screen.getByTestId('set-contrast-high').click();
      });

      expect(document.documentElement.getAttribute('data-contrast')).toBe('high');
      expect(screen.getByTestId('contrast-preference')).toHaveTextContent('high');
      expect(screen.getByTestId('high-contrast-active')).toHaveTextContent('true');
      expect(localStorageMock.setItem).toHaveBeenCalledWith('pf-contrast', 'high');
    });

    it('manual override forces data-contrast="normal" even with the OS signal on', async () => {
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
      expect(document.documentElement.getAttribute('data-contrast')).toBe('high');

      await act(async () => {
        screen.getByTestId('set-contrast-normal').click();
      });

      expect(document.documentElement.getAttribute('data-contrast')).toBe('normal');
      expect(screen.getByTestId('high-contrast-active')).toHaveTextContent('false');
    });

    it('"system" preference defers back to the OS signal', async () => {
      renderWithThemeProvider(<TestComponent />);

      await act(async () => {
        screen.getByTestId('set-contrast-high').click();
      });
      expect(document.documentElement.getAttribute('data-contrast')).toBe('high');

      await act(async () => {
        screen.getByTestId('set-contrast-system').click();
      });

      // OS signal was mocked false in this test's default beforeEach setup.
      expect(document.documentElement.getAttribute('data-contrast')).toBe('normal');
      expect(screen.getByTestId('contrast-preference')).toHaveTextContent('system');
    });

    it('loads a persisted contrast preference from localStorage on mount', () => {
      localStorageMock.getItem.mockImplementation((key: string) =>
        key === 'pf-contrast' ? 'high' : null,
      );

      renderWithThemeProvider(<TestComponent />);

      expect(screen.getByTestId('contrast-preference')).toHaveTextContent('high');
      expect(document.documentElement.getAttribute('data-contrast')).toBe('high');
    });

    it('tracks a live prefers-contrast change while preference is "system"', async () => {
      // Proves the actual subscription wiring, not just the initial-mount
      // value: data-contrast must follow the live OS signal when no manual
      // override is set, by firing the real matchMedia 'change' handler
      // ThemeContext registers for '(prefers-contrast: more)'.
      const contrastListeners: Array<(e: MediaQueryListEvent) => void> = [];
      const mockMatchMedia = vi.fn().mockImplementation((query: string) => {
        const mediaQueryList = {
          matches: false,
          media: query,
          onchange: null,
          addEventListener: vi.fn((event: string, handler: (e: MediaQueryListEvent) => void) => {
            if (query === '(prefers-contrast: more)' && event === 'change') {
              contrastListeners.push(handler);
            }
          }),
          removeEventListener: vi.fn(),
          dispatchEvent: vi.fn(),
        };
        return mediaQueryList;
      });
      window.matchMedia = mockMatchMedia;

      renderWithThemeProvider(<TestComponent />);

      expect(document.documentElement.getAttribute('data-contrast')).toBe('normal');
      expect(contrastListeners.length).toBeGreaterThan(0);

      await act(async () => {
        contrastListeners.forEach((handler) => handler({ matches: true } as MediaQueryListEvent));
      });

      expect(screen.getByTestId('high-contrast')).toHaveTextContent('true');
      expect(screen.getByTestId('high-contrast-active')).toHaveTextContent('true');
      expect(document.documentElement.getAttribute('data-contrast')).toBe('high');

      await act(async () => {
        contrastListeners.forEach((handler) => handler({ matches: false } as MediaQueryListEvent));
      });

      expect(document.documentElement.getAttribute('data-contrast')).toBe('normal');
      expect(screen.getByTestId('high-contrast-active')).toHaveTextContent('false');
    });

    it('pins a manual override against a live OS prefers-contrast change', async () => {
      // The manual override must not be knocked out by the OS signal firing
      // in either direction while an explicit preference is set.
      const contrastListeners: Array<(e: MediaQueryListEvent) => void> = [];
      const mockMatchMedia = vi.fn().mockImplementation((query: string) => {
        const mediaQueryList = {
          matches: false,
          media: query,
          onchange: null,
          addEventListener: vi.fn((event: string, handler: (e: MediaQueryListEvent) => void) => {
            if (query === '(prefers-contrast: more)' && event === 'change') {
              contrastListeners.push(handler);
            }
          }),
          removeEventListener: vi.fn(),
          dispatchEvent: vi.fn(),
        };
        return mediaQueryList;
      });
      window.matchMedia = mockMatchMedia;

      renderWithThemeProvider(<TestComponent />);

      // Hard-assert the listener was actually registered before relying on
      // firing it — otherwise an empty contrastListeners array would make
      // this test pass vacuously even if the real subscription were broken.
      expect(contrastListeners.length).toBeGreaterThan(0);

      await act(async () => {
        screen.getByTestId('set-contrast-normal').click();
      });
      expect(document.documentElement.getAttribute('data-contrast')).toBe('normal');

      // OS signal flips to "prefers more contrast" — the live event must
      // still be processed (raw OS preference updates), but the override
      // must win precedence over it for the applied data-contrast attribute
      // and derived highContrastActive value.
      await act(async () => {
        contrastListeners.forEach((handler) => handler({ matches: true } as MediaQueryListEvent));
      });
      expect(screen.getByTestId('high-contrast')).toHaveTextContent('true');
      expect(screen.getByTestId('high-contrast-active')).toHaveTextContent('false');
      expect(document.documentElement.getAttribute('data-contrast')).toBe('normal');
      expect(screen.getByTestId('contrast-preference')).toHaveTextContent('normal');
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
      expect(screen.getByTestId('hook-contrast-preference')).toHaveTextContent('system');
      expect(screen.getByTestId('hook-high-contrast-active')).toHaveTextContent('true');
    });
  });
});
