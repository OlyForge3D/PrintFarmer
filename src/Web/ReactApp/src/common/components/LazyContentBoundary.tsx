import React, { Component, Suspense, type ErrorInfo, type ReactNode } from 'react';
import { Button } from '@/common/components/ui';

interface LazyContentErrorBoundaryProps {
  children: ReactNode;
  label: string;
  onRetry: () => void;
}

interface LazyContentErrorBoundaryState {
  error: Error | null;
}

class LazyContentErrorBoundary extends Component<
  LazyContentErrorBoundaryProps,
  LazyContentErrorBoundaryState
> {
  state: LazyContentErrorBoundaryState = { error: null };

  static getDerivedStateFromError(error: Error): LazyContentErrorBoundaryState {
    return { error };
  }

  componentDidCatch(error: Error, errorInfo: ErrorInfo) {
    void error;
    void errorInfo;
    // Recovery remains local so the route and any unsaved form state stay mounted.
  }

  private handleRetry = () => {
    this.props.onRetry();
    this.setState({ error: null });
  };

  render() {
    if (this.state.error) {
      return (
        <div className="flex min-h-48 flex-col items-center justify-center gap-3" role="alert">
          <p>Unable to load {this.props.label}.</p>
          <Button type="button" variant="primary" onClick={this.handleRetry}>
            Retry
          </Button>
        </div>
      );
    }

    return this.props.children;
  }
}

interface LazyContentBoundaryProps {
  children: ReactNode;
  fallback: ReactNode;
  label: string;
  onRetry: () => void;
}

export function LazyContentBoundary({
  children,
  fallback,
  label,
  onRetry,
}: LazyContentBoundaryProps) {
  return (
    <LazyContentErrorBoundary label={label} onRetry={onRetry}>
      <Suspense fallback={fallback}>{children}</Suspense>
    </LazyContentErrorBoundary>
  );
}
