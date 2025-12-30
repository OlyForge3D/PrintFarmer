/* eslint-disable local/pf-no-unguarded-console */
import React from 'react';

/**
 * Safely render an unknown value as a React node for debug/display purposes.
 * - null/undefined -> null
 * - React element -> returned unchanged
 * - primitive (string/number/boolean) -> string
 * - object/array -> pretty-printed JSON inside a <pre>
 * - fallback -> String(value)
 */
export function renderUnknown(value: unknown): React.ReactNode {
  if (value == null) return null;
  // If it's already a React element, render as-is
  if (React.isValidElement(value)) return value;

  const t = typeof value;
  if (t === 'string' || t === 'number' || t === 'boolean') return String(value);

  if (t === 'object') {
    try {
      return (
        <pre className="mt-1 text-xs text-pf-text-secondary bg-pf-bg-1 p-2 rounded border border-pf-border overflow-x-auto">
          {JSON.stringify(value, null, 2)}
        </pre>
      );
    } catch {
      return String(value);
    }
  }

  return String(value);
}

export default renderUnknown;
