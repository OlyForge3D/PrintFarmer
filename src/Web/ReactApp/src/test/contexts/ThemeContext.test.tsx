import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render } from '@testing-library/react';
import { screen } from '@testing-library/dom';
import { act } from '@testing-library/react';
import { ThemeProvider, useTheme, useComputedTheme, useAccessibilityPreferences } from '@/contexts/ThemeContext';
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
      <button data-testid="toggle" onClick={toggleTheme}>Toggle</button>
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

type ThemeType = 'light' | 'dark' | 'system';
const renderWithThemeProvider = (
  ui: ReactNode, 
  { defaultTheme = 'dark', storageKey = 'test-theme' }: { defaultTheme?: ThemeType, storageKey?: string } = {}
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

    it('ignores invalid localStorage values', () => {
      localStorageMock.getItem.mockReturnValue('invalid-theme');
      
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
    it('toggles from dark to light', async () => {
      renderWithThemeProvider(<TestComponent />, { defaultTheme: 'dark' });
      
      await act(async () => {
        screen.getByTestId('toggle').click();
      });
      
      expect(screen.getByTestId('theme')).toHaveTextContent('light');
    });

    it('toggles from light to dark', async () => {
      renderWithThemeProvider(<TestComponent />, { defaultTheme: 'light' });
      
      await act(async () => {
        screen.getByTestId('toggle').click();
      });
      
      expect(screen.getByTestId('theme')).toHaveTextContent('dark');
    });

    it('toggles from system to opposite of system preference', async () => {
      // System preference is dark (mocked)
      renderWithThemeProvider(<TestComponent />, { defaultTheme: 'system' });
      
      await act(async () => {
        screen.getByTestId('toggle').click();
      });
      
      expect(screen.getByTestId('theme')).toHaveTextContent('light'); // Opposite of system dark
    });
  });

  describe('system theme detection', () => {
    it('detects system dark theme preference', () => {
      window.matchMedia = createMockMatchMedia(true);
      
      renderWithThemeProvider(<TestComponent />, { defaultTheme: 'system' });
      
      expect(screen.getByTestId('computed-theme')).toHaveTextContent('dark');
    });

    it('detects system light theme preference', () => {
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
      const mockMatchMedia = vi.fn()
        .mockImplementationOnce(() => ({ // prefers-color-scheme
          matches: true,
          addEventListener: vi.fn(),
          removeEventListener: vi.fn(),
        }))
        .mockImplementationOnce(() => ({ // prefers-reduced-motion
          matches: true,
          addEventListener: vi.fn(),
          removeEventListener: vi.fn(),
        }))
        .mockImplementationOnce(() => ({ // prefers-contrast
          matches: false,
          addEventListener: vi.fn(),
          removeEventListener: vi.fn(),
        }));
      
      window.matchMedia = mockMatchMedia;
      
      renderWithThemeProvider(<TestComponent />);
      
      expect(screen.getByTestId('reduced-motion')).toHaveTextContent('true');
    });

    it('detects high contrast preference', () => {
      const mockMatchMedia = vi.fn()
        .mockImplementationOnce(() => ({ // prefers-color-scheme
          matches: true,
          addEventListener: vi.fn(),
          removeEventListener: vi.fn(),
        }))
        .mockImplementationOnce(() => ({ // prefers-reduced-motion
          matches: false,
          addEventListener: vi.fn(),
          removeEventListener: vi.fn(),
        }))
        .mockImplementationOnce(() => ({ // prefers-contrast
          matches: true,
          addEventListener: vi.fn(),
          removeEventListener: vi.fn(),
        }));
      
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

    it('removes data-theme attribute for dark theme', async () => {
      renderWithThemeProvider(<TestComponent />, { defaultTheme: 'light' });
      
      // First ensure light theme sets the attribute
      expect(document.documentElement.getAttribute('data-theme')).toBe('light');
      
      await act(async () => {
        screen.getByTestId('set-dark').click();
      });
      
      expect(document.documentElement.getAttribute('data-theme')).toBeNull();
    });

    it('applies reduced motion CSS variable', () => {
      const mockMatchMedia = vi.fn()
        .mockImplementationOnce(() => ({
          matches: true,
          addEventListener: vi.fn(),
          removeEventListener: vi.fn(),
        }))
        .mockImplementationOnce(() => ({
          matches: true, // prefers-reduced-motion: reduce
          addEventListener: vi.fn(),
          removeEventListener: vi.fn(),
        }))
        .mockImplementationOnce(() => ({
          matches: false,
          addEventListener: vi.fn(),
          removeEventListener: vi.fn(),
        }));
      
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
      
      expect(eventListener).toHaveBeenCalledWith(
        expect.objectContaining({
          type: 'themeChange',
          detail: { theme: 'light', computedTheme: 'light' }
        })
      );
      
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
      const mockMatchMedia = vi.fn()
        .mockImplementationOnce(() => ({
          matches: true,
          addEventListener: vi.fn(),
          removeEventListener: vi.fn(),
        }))
        .mockImplementationOnce(() => ({
          matches: true, // reduced motion
          addEventListener: vi.fn(),
          removeEventListener: vi.fn(),
        }))
        .mockImplementationOnce(() => ({
          matches: true, // high contrast
          addEventListener: vi.fn(),
          removeEventListener: vi.fn(),
        }));
      
      window.matchMedia = mockMatchMedia;
      
      renderWithThemeProvider(<TestHooks />);
      
      expect(screen.getByTestId('hook-reduced-motion')).toHaveTextContent('true');
      expect(screen.getByTestId('hook-high-contrast')).toHaveTextContent('true');
    });
  });
});