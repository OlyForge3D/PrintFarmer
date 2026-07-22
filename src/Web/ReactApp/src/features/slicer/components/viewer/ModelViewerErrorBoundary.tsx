import React, { Component } from 'react';
import type { ErrorInfo, ReactNode } from 'react';
import { Button } from '@/common/components/ui';

interface ModelViewerErrorBoundaryProps {
  children: ReactNode;
  className?: string;
  resetKey?: string;
  fallback?: ReactNode;
}

interface ModelViewerErrorBoundaryState {
  hasError: boolean;
  retryNonce: number;
}

export class ModelViewerErrorBoundary extends Component<ModelViewerErrorBoundaryProps, ModelViewerErrorBoundaryState> {
  constructor(props: ModelViewerErrorBoundaryProps) {
    super(props);
    this.state = {
      hasError: false,
      retryNonce: 0,
    };
  }

  static getDerivedStateFromError(): Partial<ModelViewerErrorBoundaryState> {
    return {
      hasError: true,
    };
  }

  componentDidCatch(error: Error, errorInfo: ErrorInfo) {
    console.error('ModelViewerErrorBoundary caught an error:', error, errorInfo);
  }

  componentDidUpdate(prevProps: ModelViewerErrorBoundaryProps) {
    if (this.props.resetKey !== prevProps.resetKey && this.state.hasError) {
      this.setState({ hasError: false });
    }
  }

  private handleRetry = () => {
    this.setState((prevState) => ({
      hasError: false,
      retryNonce: prevState.retryNonce + 1,
    }));
  };

  render() {
    if (this.state.hasError) {
      if (this.props.fallback) {
        return <React.Fragment key={this.state.retryNonce}>{this.props.fallback}</React.Fragment>;
      }

      return (
        <div
          className={`flex h-full w-full items-center justify-center bg-pf-bg-0 p-4 ${this.props.className ?? ''}`.trim()}
          role="alert"
        >
          <div className="max-w-sm rounded-lg border border-pf-border bg-pf-bg-1 p-6 text-center shadow-lg">
            <h3 className="text-sm font-semibold text-pf-text-primary">Failed to load 3D model</h3>
            <p className="mt-2 text-sm text-pf-text-secondary">Try again or select a different model.</p>
            <div className="mt-4 flex justify-center">
              <Button variant="primary" size="sm" onClick={this.handleRetry}>
                Retry
              </Button>
            </div>
          </div>
        </div>
      );
    }

    return <React.Fragment key={this.state.retryNonce}>{this.props.children}</React.Fragment>;
  }
}

export default ModelViewerErrorBoundary;
