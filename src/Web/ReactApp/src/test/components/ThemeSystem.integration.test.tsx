import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render } from '@testing-library/react';
import { screen, fireEvent } from '@testing-library/dom';
import { act } from '@testing-library/react';
import { ThemeProvider, useTheme } from '@/contexts/ThemeContext';
import { ThemeToggle } from '@/common/components/ThemeToggle';

// Mock localStorage
const localStorageMock = {
  getItem: vi.fn(),
  setItem: vi.fn(),
  removeItem: vi.fn(),
  clear: vi.fn(),
};
Object.defineProperty(window, 'localStorage', { value: localStorageMock });

// Test app component that demonstrates core theme functionality
function TestApp() {
  const { theme, computedTheme } = useTheme();
  
  return (
    <div data-testid="app">
      <header>
        <h1>PrintFarmer</h1>
        <ThemeToggle data-testid="theme-toggle" />
      </header>
      
      <main>
        <div data-testid="theme-info">
          <span data-testid="current-theme">Theme: {theme}</span>
          <span data-testid="computed-theme">Computed: {computedTheme}</span>
        </div>
      </main>
    </div>
  );
}

describe('Theme System Integration', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorageMock.getItem.mockReturnValue(null);
    
    // Mock matchMedia for all preference queries
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
    document.documentElement.removeAttribute('data-theme');
  });

  it('initializes with dark theme by default', () => {
    render(
      <ThemeProvider defaultTheme="dark">
        <TestApp />
      </ThemeProvider>
    );
    
    expect(screen.getByTestId('current-theme')).toHaveTextContent('Theme: dark');
    expect(screen.getByTestId('computed-theme')).toHaveTextContent('Computed: dark');
  });

  it('switches themes using toggle component', async () => {
    render(
      <ThemeProvider defaultTheme="dark">
        <TestApp />
      </ThemeProvider>
    );
    
    const toggle = screen.getByRole('button');
    
    // Start with dark
    expect(screen.getByTestId('current-theme')).toHaveTextContent('Theme: dark');
    
    // Click to cycle to next theme (dark -> light -> system -> repeat)
    await act(async () => {
      fireEvent.click(toggle);
    });
    
    // After clicking once, should have changed theme (verify it's different)
    const newTheme = screen.getByTestId('current-theme').textContent;
    expect(newTheme).not.toBe('Theme: dark');
  });

  it('persists theme changes to localStorage', async () => {
    render(
      <ThemeProvider defaultTheme="dark" storageKey="integration-test">
        <TestApp />
      </ThemeProvider>
    );
    
    const toggle = screen.getByRole('button');
    
    await act(async () => {
      fireEvent.click(toggle); // Switch to next theme
    });
    
    // Should have persisted to localStorage (any theme change)
    expect(localStorageMock.setItem).toHaveBeenCalledWith('integration-test', expect.any(String));
  });

  it('loads theme from localStorage on initialization', () => {
    localStorageMock.getItem.mockReturnValue('light');
    
    render(
      <ThemeProvider storageKey="integration-test">
        <TestApp />
      </ThemeProvider>
    );
    
    expect(localStorageMock.getItem).toHaveBeenCalledWith('integration-test');
    expect(screen.getByTestId('current-theme')).toHaveTextContent('Theme: light');
  });

  it('handles system theme properly', () => {
    // Mock system preference for light
    window.matchMedia = vi.fn().mockImplementation((query) => {
      if (query.includes('prefers-color-scheme')) {
        return { matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() }; // false = light
      }
      return { matches: false, addEventListener: vi.fn(), removeEventListener: vi.fn() };
    });
    
    render(
      <ThemeProvider defaultTheme="system">
        <TestApp />
      </ThemeProvider>
    );
    
    expect(screen.getByTestId('current-theme')).toHaveTextContent('Theme: system');
    expect(screen.getByTestId('computed-theme')).toHaveTextContent('Computed: light');
  });

  it('applies DOM changes when switching themes to light', async () => {
    render(
      <ThemeProvider defaultTheme="light">
        <TestApp />
      </ThemeProvider>
    );
    
    // Light theme should have data-theme attribute
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    
    // Switch to dark
    const toggle = screen.getByRole('button');
    await act(async () => {
      fireEvent.click(toggle);
    });
    
    // After switching, should be different
    const newTheme = document.documentElement.getAttribute('data-theme');
    expect(newTheme).not.toBe('light');
  });

  it('cycles through all themes correctly', async () => {
    render(
      <ThemeProvider defaultTheme="light">
        <TestApp />
      </ThemeProvider>
    );
    
    const toggle = screen.getByRole('button');
    
    // Start: light
    expect(screen.getByTestId('current-theme')).toHaveTextContent('Theme: light');
    
    // Click 1: light -> github-dark
    await act(async () => {
      fireEvent.click(toggle);
    });
    expect(screen.getByTestId('current-theme')).toHaveTextContent('Theme: github-dark');
    
    // Click 2: github-dark -> printfarmer-dark
    await act(async () => {
      fireEvent.click(toggle);
    });
    expect(screen.getByTestId('current-theme')).toHaveTextContent('Theme: printfarmer-dark');
    
    // Click 3: printfarmer-dark -> system
    await act(async () => {
      fireEvent.click(toggle);
    });
    expect(screen.getByTestId('current-theme')).toHaveTextContent('Theme: system');
    
    // Click 4: system -> light
    await act(async () => {
      fireEvent.click(toggle);
    });
    expect(screen.getByTestId('current-theme')).toHaveTextContent('Theme: light');
  });

  it('dispatches theme change events', async () => {
    const eventListener = vi.fn();
    window.addEventListener('themeChange', eventListener);
    
    render(
      <ThemeProvider defaultTheme="dark">
        <TestApp />
      </ThemeProvider>
    );
    
    const toggle = screen.getByRole('button');
    
    await act(async () => {
      fireEvent.click(toggle);
    });
    
    // Should have dispatched a theme change event
    expect(eventListener).toHaveBeenCalled();
    
    window.removeEventListener('themeChange', eventListener);
  });
});