import { Component } from 'react';
import type { ReactNode } from 'react';
import { renderUnknown } from '@/common/utils/renderUnknown';
import { Button } from '@/common/components/ui';

interface Props {
  children: ReactNode;
}

interface State {
  hasError: boolean;
  error: Error | null;
}

export class ErrorBoundary extends Component<Props, State> {
  constructor(props: Props) {
    super(props);
    this.state = { hasError: false, error: null };
  }

  static getDerivedStateFromError(error: Error): State {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, errorInfo: React.ErrorInfo) {
    console.error('ErrorBoundary caught an error:', error, errorInfo);
  }

  render() {
    if (this.state.hasError) {
      return (
        <div className="min-h-screen flex items-center justify-center bg-pf-bg-0">
          <div className="max-w-md w-full bg-pf-bg-1 shadow-lg rounded-lg p-6 border border-pf-border">
            <div className="flex items-center mb-4">
              <div className="shrink-0">
                <svg className="h-8 w-8 text-pf-error" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.966-.833-2.732 0L3.732 16.5c-.77.833.192 2.5 1.732 2.5z" />
                </svg>
              </div>
              <div className="ml-3">
                <h3 className="text-sm font-medium text-pf-text-primary">
                  Something went wrong
                </h3>
              </div>
            </div>
            
            <div className="text-sm text-pf-text-secondary mb-4">
              <p>An unexpected error occurred. Please try refreshing the page.</p>
              {this.state.error && (
                <details className="mt-2">
                  <summary className="cursor-pointer text-pf-text-primary font-medium">
                    Error Details
                  </summary>
                  <div className="mt-2">
                    {renderUnknown({ message: this.state.error.message, stack: (this.state.error as Error).stack })}
                  </div>
                </details>
              )}
            </div>
            
            <Button
              variant="primary"
              onClick={() => window.location.reload()}
              className="w-full"
            >
              Refresh Page
            </Button>
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}

/**
 * Detects whether an error originated from a failed dynamic `import()` used
 * by `React.lazy` code-splitting.
 *
 * When such a promise rejects, `React.lazy` caches the rejection permanently
 * on the module registry — subsequent renders of the same lazy component
 * re-throw the cached rejection, so a plain "Try Again" (clear boundary
 * state) will immediately re-crash. The only reliable recovery in that
 * scenario is a full page reload, which recreates the module registry.
 *
 * Detection has to cover multiple environments because bundlers phrase the
 * error differently:
 *  - webpack:         `Error.name === 'ChunkLoadError'`,
 *                     "Loading chunk NN failed"
 *  - Vite (fetch):    `TypeError: Failed to fetch dynamically imported
 *                     module: <url>`
 *  - Vite (CSS):      "Unable to preload CSS for <url>" — thrown from
 *                     Vite's importAnalysisBuild plugin when a lazy
 *                     route's stylesheet 404s (typically a stale
 *                     `assets/<hash>.css` that has been rebuilt server-
 *                     side). React re-raises this as a render-time
 *                     exception because it happens inside the lazy
 *                     wrapper's suspense boundary, and it must be
 *                     classified as chunk-load or the operator sees
 *                     "Try Again" (which will re-crash) instead of
 *                     "Reload page" (which recovers).
 *  - Safari/Firefox:  "Importing a module script failed" or
 *                     "Failed to load module script"
 *  - CSS chunk load:  "Loading CSS chunk NN failed" (webpack) or
 *                     "error loading CSS chunk" (Vite alt phrasing)
 */
function isChunkLoadError(err: Error | null | undefined): boolean {
  if (!err) return false;
  const name = err.name ?? '';
  const msg = err.message ?? '';
  return (
    name === 'ChunkLoadError' ||
    /Loading chunk \d+ failed/i.test(msg) ||
    /Loading CSS chunk \d+ failed/i.test(msg) ||
    /error loading CSS chunk/i.test(msg) ||
    /Failed to fetch dynamically imported module/i.test(msg) ||
    /Importing a module script failed/i.test(msg) ||
    /Failed to load module script/i.test(msg) ||
    /error loading dynamically imported module/i.test(msg) ||
    /Unable to preload CSS/i.test(msg)
  );
}

interface RouteErrorBoundaryProps extends Props {
  /**
   * Any serializable value whose change signals a scope reset. When it
   * changes between renders, an errored boundary clears its state so a
   * subsequent render of `children` can try again.
   *
   * The route-level `Layout` passes the current pathname so that
   * navigating to a healthy route via the sidebar always clears a
   * previously-errored route boundary — the operator does not have to
   * hit "Try Again" first, which is important because the errored
   * component tree is what raised the crash (a rejected `React.lazy`
   * promise, etc.) and navigating away already replaces those children
   * entirely.
   */
  resetKey?: unknown;
}

/**
 * Route-scoped error boundary for catching page-level crashes without
 * tearing down the app shell (sidebar + header remain navigable).
 *
 * Recovery strategy:
 *  1. **Location-based reset**: `resetKey` is passed by the app shell
 *     (usually the current pathname). When it changes, an errored
 *     boundary clears its state on the next render so navigating to a
 *     different route recovers automatically — the user does NOT need
 *     to click "Try Again" first, and there is no infinite reload loop.
 *  2. **Chunk-load errors** (rejected `React.lazy` imports) require a
 *     full page reload because the module registry has cached the
 *     rejection. We surface an explicit "Reload page" button, clearly
 *     labelled, so the operator opts in — we never call
 *     `window.location.reload()` automatically.
 *  3. **All other errors** show the standard "Try Again" that clears
 *     the boundary state so the children re-render (useful for
 *     transient render-time exceptions that will not reproduce on the
 *     next attempt).
 */
export class RouteErrorBoundary extends Component<RouteErrorBoundaryProps, State> {
  constructor(props: RouteErrorBoundaryProps) {
    super(props);
    this.state = { hasError: false, error: null };
  }

  static getDerivedStateFromError(error: Error): State {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, errorInfo: React.ErrorInfo) {
    console.error('RouteErrorBoundary caught an error:', error, errorInfo);
  }

  componentDidUpdate(prevProps: RouteErrorBoundaryProps) {
    // Clear a stuck error when the reset key changes. This is what makes
    // sidebar navigation a real recovery path — the caller (Layout) passes
    // the pathname, so any navigation shifts `resetKey` and the boundary
    // re-attempts render with the new route's children.
    if (this.state.hasError && prevProps.resetKey !== this.props.resetKey) {
      this.setState({ hasError: false, error: null });
    }
  }

  private handleTryAgain = () => {
    this.setState({ hasError: false, error: null });
  };

  private handleReload = () => {
    // Full reload is required for chunk-load errors because `React.lazy`
    // caches the rejected import promise on the module registry — merely
    // re-mounting the component will re-throw the cached rejection.
    // Reloading recreates the registry and re-fetches the chunk from the
    // network. Wrapped in a guard so tests / SSR-like environments where
    // reload is a stubbed no-op still exercise the same path.
    //
    // A plain reload is not enough when the service worker is serving a
    // previous build's chunks cache-first: the reload replays the same stale
    // modules and fails identically, which is why only a *hard* reload used
    // to recover. Purge the SW caches (best effort, never blocking) so the
    // reload refetches from the network.
    void this.purgeCachesThenReload();
  };

  private async purgeCachesThenReload() {
    try {
      if (typeof caches !== 'undefined') {
        const keys = await caches.keys();
        await Promise.all(
          keys
            .filter((key) => key.startsWith('pf-shell-') || key.startsWith('pf-runtime-'))
            .map((key) => caches.delete(key))
        );
      }
    } catch {
      // Cache purge is best effort — reload regardless.
    }
    if (typeof window !== 'undefined' && window.location) {
      window.location.reload();
    }
  }

  render() {
    if (this.state.hasError) {
      const chunkError = isChunkLoadError(this.state.error);
      return (
        <div className="flex flex-col items-center justify-center min-h-[50vh] px-6" data-testid="route-error-boundary">
          <div className="max-w-md w-full bg-pf-bg-1 shadow-lg rounded-lg p-6 border border-pf-border">
            <div className="flex items-center mb-4">
              <svg className="h-8 w-8 text-pf-error shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.966-.833-2.732 0L3.732 16.5c-.77.833.192 2.5 1.732 2.5z" />
              </svg>
              <h3 className="ml-3 text-sm font-medium text-pf-text-primary">
                {chunkError ? 'Failed to load this page' : 'This page encountered an error'}
              </h3>
            </div>

            <p className="text-sm text-pf-text-secondary mb-4">
              {chunkError
                ? 'A background code chunk could not be loaded, which usually happens after a new deployment or a brief network hiccup. Reloading the page fetches a fresh copy. You can also switch to another page using the sidebar.'
                : 'Something went wrong loading this page. You can try reloading or navigate to another page using the sidebar.'}
            </p>

            {this.state.error && (
              <details className="mb-4 text-sm text-pf-text-secondary">
                <summary className="cursor-pointer text-pf-text-primary font-medium">
                  Error Details
                </summary>
                <div className="mt-2 max-h-32 overflow-auto text-xs">
                  {renderUnknown({ message: this.state.error.message })}
                </div>
              </details>
            )}

            {chunkError ? (
              <Button
                variant="primary"
                onClick={this.handleReload}
                className="w-full"
                data-testid="route-error-reload-button"
              >
                Reload page
              </Button>
            ) : (
              <Button
                variant="primary"
                onClick={this.handleTryAgain}
                className="w-full"
                data-testid="route-error-try-again-button"
              >
                Try Again
              </Button>
            )}
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}