import { useCallback, useRef, useEffect, useMemo } from 'react';
import { useSearchParams } from 'react-router';

type ParamType = 'string' | 'number' | 'boolean';

interface ParamDefinition<T> {
  key: string;
  type: ParamType;
  defaultValue: T;
  /** Debounce delay in ms for URL updates (useful for text inputs). 0 = immediate. */
  debounce?: number;
  /** Whether this param is a user-facing filter (vs navigation/sort). Affects hasActiveFilters and resetAll. Defaults to true. */
  filterable?: boolean;
}

type ParamConfig = Record<string, ParamDefinition<string | number | boolean>>;

type FilterValues<C extends ParamConfig> = {
  [K in keyof C]: C[K]['defaultValue'];
};

type FilterSetters<C extends ParamConfig> = {
  [K in keyof C as `set${Capitalize<string & K>}`]: (value: C[K]['defaultValue']) => void;
};

type UseUrlFilterStateReturn<C extends ParamConfig> = FilterValues<C> &
  FilterSetters<C> & {
    resetAll: () => void;
    hasActiveFilters: boolean;
    /** Batch-update multiple params in a single URL navigation (avoids React Router stale-closure issue with multiple setSearchParams calls). */
    setMany: (updates: Partial<FilterValues<C>>) => void;
  };

function parseParam(raw: string | null, type: ParamType, defaultValue: string | number | boolean): string | number | boolean {
  if (raw === null || raw === '') return defaultValue;
  switch (type) {
    case 'number': {
      const n = Number(raw);
      return Number.isFinite(n) ? n : defaultValue;
    }
    case 'boolean':
      return raw === 'true' || raw === '1';
    default:
      return raw;
  }
}

function serializeParam(value: string | number | boolean, type: ParamType, defaultValue: string | number | boolean): string | undefined {
  if (value === defaultValue) return undefined;
  switch (type) {
    case 'boolean':
      return value ? 'true' : undefined;
    case 'number':
      return String(value);
    default:
      return value ? String(value) : undefined;
  }
}

/**
 * Manages filter state via URL search params so users can bookmark/share filtered views.
 * Default values are omitted from the URL to keep it clean.
 * Supports optional debounce for text fields to avoid URL thrashing.
 */
export function useUrlFilterState<C extends ParamConfig>(config: C): UseUrlFilterStateReturn<C> {
  const [searchParams, setSearchParams] = useSearchParams();
  const debounceTimers = useRef<Record<string, ReturnType<typeof setTimeout>>>({});

  // Clean up debounce timers on unmount
  useEffect(() => {
    const timers = debounceTimers.current;
    return () => {
      for (const key of Object.keys(timers)) {
        clearTimeout(timers[key]);
      }
    };
  }, []);

  const configEntries = useMemo(() => Object.entries(config), [config]);

  const values = useMemo(() => {
    const result = {} as Record<string, string | number | boolean>;
    for (const [name, def] of configEntries) {
      result[name] = parseParam(searchParams.get(def.key), def.type, def.defaultValue);
    }
    return result as FilterValues<C>;
  }, [searchParams, configEntries]);

  const updateParam = useCallback((paramKey: string, value: string | number | boolean, def: ParamDefinition<string | number | boolean>) => {
    const apply = () => {
      setSearchParams(prev => {
        const next = new URLSearchParams(prev);
        const serialized = serializeParam(value, def.type, def.defaultValue);
        if (serialized === undefined) {
          next.delete(def.key);
        } else {
          next.set(def.key, serialized);
        }
        return next;
      }, { replace: true });
    };

    const debounceMs = def.debounce ?? 0;
    if (debounceMs > 0) {
      clearTimeout(debounceTimers.current[paramKey]);
      debounceTimers.current[paramKey] = setTimeout(apply, debounceMs);
    } else {
      apply();
    }
  }, [setSearchParams]);

  const setters = useMemo(() => {
    const result = {} as Record<string, (value: string | number | boolean) => void>;
    for (const [name, def] of configEntries) {
      const capitalizedName = name.charAt(0).toUpperCase() + name.slice(1);
      result[`set${capitalizedName}`] = (value: string | number | boolean) => {
        updateParam(name, value, def);
      };
    }
    return result as FilterSetters<C>;
  }, [updateParam, configEntries]);

  const resetAll = useCallback(() => {
    for (const key of Object.keys(debounceTimers.current)) {
      clearTimeout(debounceTimers.current[key]);
    }
    setSearchParams(prev => {
      const next = new URLSearchParams(prev);
      for (const [, def] of configEntries) {
        if (def.filterable !== false) {
          next.delete(def.key);
        }
      }
      return next;
    }, { replace: true });
  }, [setSearchParams, configEntries]);

  const setMany = useCallback((updates: Partial<FilterValues<C>>) => {
    for (const name of Object.keys(updates)) {
      if (debounceTimers.current[name]) {
        clearTimeout(debounceTimers.current[name]);
      }
    }
    setSearchParams(prev => {
      const next = new URLSearchParams(prev);
      for (const [name, def] of configEntries) {
        if (!(name in (updates as Record<string, unknown>))) continue;
        const value = (updates as Record<string, string | number | boolean>)[name];
        const serialized = serializeParam(value, def.type, def.defaultValue);
        if (serialized === undefined) {
          next.delete(def.key);
        } else {
          next.set(def.key, serialized);
        }
      }
      return next;
    }, { replace: true });
  }, [setSearchParams, configEntries]);

  const hasActiveFilters = useMemo(() => {
    for (const [name, def] of configEntries) {
      if (def.filterable === false) continue;
      if (values[name as keyof typeof values] !== def.defaultValue) return true;
    }
    return false;
  }, [values, configEntries]);

  return { ...values, ...setters, setMany, resetAll, hasActiveFilters } as UseUrlFilterStateReturn<C>;
}
