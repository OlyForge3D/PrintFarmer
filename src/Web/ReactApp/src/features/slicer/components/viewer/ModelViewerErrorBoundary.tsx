import React, { Component } from 'react';
import type { ErrorInfo, ReactNode } from 'react';
import { Button } from '@/common/components/ui';

/**
 * Content for the per-model "failed to load" fallback shown inside drei's
 * `<Html center>` overlay in {@link SlicerBedVisualization}.
 *
 * Issue #1974: drei's `<Html>` positions its content with nested
 * `position: absolute` wrappers that have no explicit `left`/`right`/`width`.
 * Those wrappers fall back to CSS shrink-to-fit sizing for their `auto`
 * width, which — for a transformed/centered absolute box — can resolve to a
 * near-zero available width and force the text to wrap one word (or
 * character) per line on narrow (390px) viewports. A `max-w-*` alone doesn't
 * fix this because it only caps the width, it doesn't establish one; an
 * explicit `w-*` fixes the box to a real pixel width regardless of the
 * wrappers' shrink-to-fit result, so it renders identically on mobile and
 * desktop viewports. Extracted as its own component so this contract can be
 * covered by a direct render test without mounting the full three.js scene.
 */
export function ModelLoadFailedAlert() {
  return (
    <div
      className="w-64 max-w-[80vw] rounded-lg border border-pf-border bg-pf-bg-1/95 px-4 py-3 text-center text-sm text-pf-text-primary shadow-lg backdrop-blur-sm"
      role="alert"
    >
      Failed to load this 3D model. Select another model or retry with a refreshed source.
    </div>
  );
}

interface ModelViewerErrorBoundaryProps {
  children: ReactNode;
  className?: string;
  resetKey?: string;
  fallback?: ReactNode;
  /** Called when a child throws (e.g. a 3D model failed to load). */
  onError?: (error: Error) => void;
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
    this.props.onError?.(error);
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
