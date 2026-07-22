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
 * Route-scoped error boundary for catching page-level crashes without
 * tearing down the app shell (sidebar + header remain navigable).
 */
export class RouteErrorBoundary extends Component<Props, State> {
  constructor(props: Props) {
    super(props);
    this.state = { hasError: false, error: null };
  }

  static getDerivedStateFromError(error: Error): State {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, errorInfo: React.ErrorInfo) {
    console.error('RouteErrorBoundary caught an error:', error, errorInfo);
  }

  render() {
    if (this.state.hasError) {
      return (
        <div className="flex flex-col items-center justify-center min-h-[50vh] px-6">
          <div className="max-w-md w-full bg-pf-bg-1 shadow-lg rounded-lg p-6 border border-pf-border">
            <div className="flex items-center mb-4">
              <svg className="h-8 w-8 text-pf-error shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.966-.833-2.732 0L3.732 16.5c-.77.833.192 2.5 1.732 2.5z" />
              </svg>
              <h3 className="ml-3 text-sm font-medium text-pf-text-primary">
                This page encountered an error
              </h3>
            </div>

            <p className="text-sm text-pf-text-secondary mb-4">
              Something went wrong loading this page. You can try reloading or navigate to another page using the sidebar.
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

            <Button
              variant="primary"
              onClick={() => this.setState({ hasError: false, error: null })}
              className="w-full"
            >
              Try Again
            </Button>
          </div>
        </div>
      );
    }

    return this.props.children;
  }
}