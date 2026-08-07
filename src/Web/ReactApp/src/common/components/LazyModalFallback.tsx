import React from 'react';

export function LazyModalFallback({ label }: { label: string }) {
  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/50"
      role="status"
      aria-live="polite"
      aria-label={label}
    >
      <div className="flex items-center gap-3 rounded-lg border border-pf-border bg-pf-bg-1 px-5 py-4 text-pf-text">
        <div className="pf-animate-spin h-6 w-6 rounded-full border-b-2 border-pf-accent" />
        <span>{label}…</span>
      </div>
    </div>
  );
}
