import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

/**
 * Compare two values for equality using structural comparison for arrays and plain
 * objects, and Object.is for everything else. Sufficient for scalar-heavy settings
 * shapes; callers with exotic value shapes can pass their own `isEqual`.
 */
/**
 * Structural comparison for settings values: scalars, arrays and plain records.
 *
 * Exported because callers that break a dirty key down further — counting which
 * individual fields inside a changed section differ, for instance — must use the
 * same rule this hook used to decide the section was dirty at all. Two
 * comparison functions would eventually disagree, and the symptom would be a
 * save bar claiming zero changes while refusing to go away.
 */
export function isStructurallyEqual(a: unknown, b: unknown): boolean {
  return defaultIsEqual(a, b);
}

function defaultIsEqual(a: unknown, b: unknown): boolean {
  if (Object.is(a, b)) return true;
  if (a === null || b === null || typeof a !== 'object' || typeof b !== 'object') return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  if (Array.isArray(a) && Array.isArray(b)) {
    if (a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) {
      if (!defaultIsEqual(a[i], b[i])) return false;
    }
    return true;
  }
  const aKeys = Object.keys(a as Record<string, unknown>);
  const bKeys = Object.keys(b as Record<string, unknown>);
  if (aKeys.length !== bKeys.length) return false;
  for (const key of aKeys) {
    if (!Object.prototype.hasOwnProperty.call(b, key)) return false;
    if (!defaultIsEqual(
      (a as Record<string, unknown>)[key],
      (b as Record<string, unknown>)[key],
    )) return false;
  }
  return true;
}

export interface UseDirtyStateOptions {
  /**
   * When true (default) install a `beforeunload` listener while `isDirty` is true so
   * the browser prompts the user before they navigate away with unsaved changes.
   * Set to false in tests or when the parent already owns unload behaviour.
   */
  guardUnload?: boolean;
  /**
   * Custom equality function used per key when computing `changedKeys`. Defaults to a
   * structural comparison suitable for scalars, arrays and plain records.
   */
  isEqual?: (a: unknown, b: unknown) => boolean;
}

export interface UseDirtyStateResult<T extends Record<string, unknown>> {
  /** Current working values (edited copy of the original). */
  values: T;
  /** Update a single field. Preserves referential stability of unaffected keys. */
  setValue: <K extends keyof T>(key: K, value: T[K]) => void;
  /** Merge multiple fields at once. */
  setValues: (partial: Partial<T>) => void;
  /** Replace the entire working set. Does not touch the original baseline. */
  replaceValues: (next: T) => void;
  /** Revert working values back to the original baseline. */
  reset: () => void;
  /**
   * Accept the current (or provided) values as the new baseline — after a
   * successful save. Clears `isDirty` and `changedKeys`.
   */
  markPristine: (next?: T) => void;
  /** True whenever any key differs from the original. */
  isDirty: boolean;
  /** The keys whose current value no longer matches the original. */
  changedKeys: (keyof T)[];
  /** `changedKeys.length`, exposed for convenience in save-bar summaries. */
  changedCount: number;
  /** The original baseline. Useful for diffing in save-bar tooltips. */
  original: T;
}

/**
 * Track dirty state for a scoped set of fields (typically one settings section).
 *
 * Usage:
 *   const state = useDirtyState(initialValues);
 *   <Input value={state.values.name} onChange={e => state.setValue('name', e.target.value)} />
 *   <AdminSaveBar isDirty={state.isDirty} changeCount={state.changedCount}
 *     onDiscard={state.reset} onSave={async () => { await save(state.values); state.markPristine(); }} />
 *
 * Reinitialising: if `initialValues` changes by reference (e.g. after a fetch resolves),
 * pass the new baseline to `markPristine(next)` explicitly. This hook does NOT auto-sync
 * on prop changes because that would silently discard user edits mid-fetch.
 */
export function useDirtyState<T extends Record<string, unknown>>(
  initialValues: T,
  options: UseDirtyStateOptions = {},
): UseDirtyStateResult<T> {
  const { guardUnload = true, isEqual = defaultIsEqual } = options;

  const [original, setOriginal] = useState<T>(initialValues);
  const [values, setValuesState] = useState<T>(initialValues);

  const setValue = useCallback(<K extends keyof T>(key: K, value: T[K]) => {
    setValuesState(prev => ({ ...prev, [key]: value }));
  }, []);

  const setValues = useCallback((partial: Partial<T>) => {
    setValuesState(prev => ({ ...prev, ...partial }));
  }, []);

  const replaceValues = useCallback((next: T) => {
    setValuesState(next);
  }, []);

  const reset = useCallback(() => {
    setValuesState(original);
  }, [original]);

  const markPristine = useCallback((next?: T) => {
    const nextBaseline = next ?? values;
    setOriginal(nextBaseline);
    setValuesState(nextBaseline);
  }, [values]);

  const changedKeys = useMemo<(keyof T)[]>(() => {
    const keys = new Set<keyof T>([
      ...(Object.keys(original) as (keyof T)[]),
      ...(Object.keys(values) as (keyof T)[]),
    ]);
    const result: (keyof T)[] = [];
    for (const key of keys) {
      if (!isEqual(original[key], values[key])) result.push(key);
    }
    return result;
  }, [original, values, isEqual]);

  const isDirty = changedKeys.length > 0;

  // beforeunload guard — installed only while dirty AND opt-in. The ref keeps the
  // handler idempotent if the effect body is re-invoked between renders while the
  // dirty flag flips rapidly.
  const isDirtyRef = useRef(isDirty);
  useEffect(() => {
    isDirtyRef.current = isDirty;
    if (!guardUnload) return;
    if (!isDirty) return;
    const handler = (event: BeforeUnloadEvent) => {
      if (!isDirtyRef.current) return;
      // Modern browsers require both preventDefault + returnValue to trigger the prompt.
      event.preventDefault();
      event.returnValue = '';
      return '';
    };
    window.addEventListener('beforeunload', handler);
    return () => window.removeEventListener('beforeunload', handler);
  }, [guardUnload, isDirty]);

  return {
    values,
    setValue,
    setValues,
    replaceValues,
    reset,
    markPristine,
    isDirty,
    changedKeys,
    changedCount: changedKeys.length,
    original,
  };
}
