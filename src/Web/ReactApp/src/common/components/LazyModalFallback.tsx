import React, {
  Component,
  Suspense,
  useEffect,
  useId,
  useRef,
  type ErrorInfo,
  type ReactNode,
} from 'react';
import { Button } from '@/common/components/ui';

interface LazyModalSurfaceProps {
  label: string;
  onCancel: () => void;
  children: ReactNode;
}

function LazyModalSurface({ label, onCancel, children }: LazyModalSurfaceProps) {
  const dialogRef = useRef<HTMLDivElement>(null);
  const onCancelRef = useRef(onCancel);
  const titleId = useId();

  useEffect(() => {
    onCancelRef.current = onCancel;
  }, [onCancel]);

  useEffect(() => {
    const getFocusableElements = () => Array.from(
      dialogRef.current?.querySelectorAll<HTMLElement>(
        'button:not([disabled]), [href], [tabindex]:not([tabindex="-1"])',
      ) ?? [],
    );
    const focusFirst = () => getFocusableElements()[0]?.focus();
    const handleFocus = (event: FocusEvent) => {
      if (!dialogRef.current?.contains(event.target as Node)) {
        focusFirst();
      }
    };
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault();
        onCancelRef.current();
        return;
      }
      if (event.key !== 'Tab') {
        return;
      }

      const focusableElements = getFocusableElements();
      if (focusableElements.length === 0) {
        event.preventDefault();
        return;
      }

      const currentIndex = focusableElements.indexOf(document.activeElement as HTMLElement);
      const nextIndex = event.shiftKey
        ? (currentIndex <= 0 ? focusableElements.length - 1 : currentIndex - 1)
        : (currentIndex === focusableElements.length - 1 ? 0 : currentIndex + 1);
      event.preventDefault();
      focusableElements[nextIndex]?.focus();
    };

    focusFirst();
    document.addEventListener('focusin', handleFocus);
    document.addEventListener('keydown', handleKeyDown);
    return () => {
      document.removeEventListener('focusin', handleFocus);
      document.removeEventListener('keydown', handleKeyDown);
    };
  }, []);

  return (
    <div
      ref={dialogRef}
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/50"
      role="dialog"
      aria-modal="true"
      aria-labelledby={titleId}
    >
      <div className="rounded-lg border border-pf-border bg-pf-bg-1 px-5 py-4 text-pf-text shadow-xl">
        <h2 id={titleId} className="sr-only">{label}</h2>
        {children}
      </div>
    </div>
  );
}

export function LazyModalFallback({ label, onCancel }: Omit<LazyModalSurfaceProps, 'children'>) {
  const loadingLabel = `Loading ${label}`;
  return (
    <LazyModalSurface label={loadingLabel} onCancel={onCancel}>
      <div className="flex items-center gap-3" role="status" aria-live="polite" aria-label={loadingLabel}>
        <div className="pf-animate-spin h-6 w-6 rounded-full border-b-2 border-pf-accent" />
        <span>{loadingLabel}…</span>
      </div>
      <Button type="button" variant="ghost" size="sm" className="mt-4 w-full" onClick={onCancel}>
        Cancel
      </Button>
    </LazyModalSurface>
  );
}

interface LazyModalErrorBoundaryProps {
  children: ReactNode;
  label: string;
  onCancel: () => void;
  onRetry: () => void;
}

interface LazyModalErrorBoundaryState {
  error: Error | null;
}

class LazyModalErrorBoundary extends Component<
  LazyModalErrorBoundaryProps,
  LazyModalErrorBoundaryState
> {
  state: LazyModalErrorBoundaryState = { error: null };

  static getDerivedStateFromError(error: Error): LazyModalErrorBoundaryState {
    return { error };
  }

  componentDidCatch(_error: Error, _errorInfo: ErrorInfo) {
    void _error;
    void _errorInfo;
    // The local recovery UI keeps the surrounding route and unsaved state mounted.
  }

  private handleRetry = () => {
    this.props.onRetry();
    this.setState({ error: null });
  };

  render() {
    if (this.state.error) {
      return (
        <LazyModalSurface label={`Unable to load ${this.props.label}`} onCancel={this.props.onCancel}>
          <div role="alert">Unable to load {this.props.label}.</div>
          <div className="mt-4 flex gap-3">
            <Button type="button" variant="primary" onClick={this.handleRetry}>
              Retry
            </Button>
            <Button type="button" variant="secondary" onClick={this.props.onCancel}>
              Cancel
            </Button>
          </div>
        </LazyModalSurface>
      );
    }

    return this.props.children;
  }
}

interface LazyModalBoundaryProps {
  children: ReactNode;
  label: string;
  onCancel: () => void;
  onRetry: () => void;
  restoreFocus?: () => void;
}

export function LazyModalBoundary({
  children,
  label,
  onCancel,
  onRetry,
  restoreFocus,
}: LazyModalBoundaryProps) {
  const restoreFocusRef = useRef(restoreFocus);
  const previousFocusRef = useRef<HTMLElement | null>(
    typeof document !== 'undefined' && document.activeElement instanceof HTMLElement
      ? document.activeElement
      : null,
  );

  useEffect(() => () => {
    if (restoreFocusRef.current) {
      restoreFocusRef.current();
      return;
    }
    if (previousFocusRef.current?.isConnected) {
      previousFocusRef.current.focus();
    }
  }, []);

  return (
    <LazyModalErrorBoundary label={label} onCancel={onCancel} onRetry={onRetry}>
      <Suspense fallback={<LazyModalFallback label={label} onCancel={onCancel} />}>
        {children}
      </Suspense>
    </LazyModalErrorBoundary>
  );
}
