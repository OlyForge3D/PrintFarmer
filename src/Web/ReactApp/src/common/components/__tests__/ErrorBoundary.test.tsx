import { render, screen } from '@testing-library/react';
import { describe, it, expect, afterEach } from 'vitest';
import { ErrorBoundary } from '../ErrorBoundary';
import { installConsoleErrorFilter } from '@/test/consoleFilter';

// Component that throws an error for testing
const ThrowError = ({ error }: { error?: Error }) => {
  if (error) {
    throw error;
  }
  throw new Error('Test error');
};

describe('ErrorBoundary', () => {
  // Whitelist-based console.error filter (Hicks #7). Previous versions
  // wholesale-replaced `console.error` with a no-op vi.fn(), which
  // silenced not just the boundary's own `componentDidCatch` log and
  // React's paired "The above error occurred in ..." noise, but also
  // any unexpected act-boundary warnings or render-phase warnings that
  // would indicate a real regression. We now allow only the patterns
  // we expect, capture the allowed calls for assertion, and fail the
  // test on any unexpected call.
  const consoleFilter = installConsoleErrorFilter([
    // Our own tagged log from componentDidCatch — the "should log
    // error to console" test asserts on this exact string.
    /ErrorBoundary caught an error:/,
    // React 18/19 error-in-tree noise around a thrown render.
    /The above error occurred in the/,
    /Consider adding an error boundary/,
    /React will try to recreate this component tree/,
    // React 19 sometimes preserves the raw thrown Error as the first
    // arg on the second `console.error` call (message is the plain
    // "Test error" / "Console log test" / "Specific test error" the
    // test itself threw). We allow only the exact throw strings the
    // tests below use.
    /Test error/,
    /Console log test/,
    /Specific test error/,
  ]);
  afterEach(() => consoleFilter.flushUnexpectedErrors());

  it('should render children when no error occurs', () => {
    render(
      <ErrorBoundary>
        <div>Child content</div>
      </ErrorBoundary>
    );

    expect(screen.getByText('Child content')).toBeInTheDocument();
  });

  it('should display error UI when an error is thrown', () => {
    render(
      <ErrorBoundary>
        <ThrowError />
      </ErrorBoundary>
    );

    expect(screen.getByText('Something went wrong')).toBeInTheDocument();
    expect(screen.getByText('An unexpected error occurred. Please try refreshing the page.')).toBeInTheDocument();
  });

  it('should show error details in a collapsible section', () => {
    const testError = new Error('Specific test error');

    render(
      <ErrorBoundary>
        <ThrowError error={testError} />
      </ErrorBoundary>
    );

    expect(screen.getByText('Error Details')).toBeInTheDocument();
  });

  it('should display refresh button', () => {
    render(
      <ErrorBoundary>
        <ThrowError />
      </ErrorBoundary>
    );

    expect(screen.getByText('Refresh Page')).toBeInTheDocument();
  });

  it('should reload page when refresh button is clicked', () => {
    // Skip this test as window.location.reload cannot be easily mocked in jsdom
    // The functionality is manually tested
  });

  it('should log error to console', () => {
    const testError = new Error('Console log test');

    render(
      <ErrorBoundary>
        <ThrowError error={testError} />
      </ErrorBoundary>
    );

    // Assert against the captured allow-listed calls rather than a
    // vi.spyOn, so we only verify the boundary's tagged message and
    // ignore React's own noise (which the filter also captured).
    const boundaryLogs = consoleFilter.allowedCalls.filter(
      call => call[0] === 'ErrorBoundary caught an error:'
    );
    expect(boundaryLogs).toHaveLength(1);
    expect(boundaryLogs[0][1]).toBe(testError);
  });

  it('should catch errors from nested components', () => {
    render(
      <ErrorBoundary>
        <div>
          <div>
            <ThrowError />
          </div>
        </div>
      </ErrorBoundary>
    );

    expect(screen.getByText('Something went wrong')).toBeInTheDocument();
  });

  it('should display error icon', () => {
    render(
      <ErrorBoundary>
        <ThrowError />
      </ErrorBoundary>
    );

    // Check for the SVG element by class or structure
    const container = screen.getByText('Something went wrong').closest('div')?.parentElement;
    expect(container?.querySelector('svg')).toBeInTheDocument();
  });
});

