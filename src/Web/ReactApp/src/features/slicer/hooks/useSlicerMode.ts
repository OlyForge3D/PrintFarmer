import { useFarmSettings } from '@/features/settings/hooks/useFarmSettings';

export type SlicerMode = 'Simple' | 'Advanced';

/**
 * SlicerMode is a UX simplification only.
 * It hides advanced controls in the browser but does NOT prevent users from
 * submitting arbitrary slicer overrides via the API directly.
 * To enforce parameter restrictions at the server level, a server-side policy
 * on the slicer submission endpoint would be required (out of scope for this feature).
 */

/**
 * Returns the current slicer mode from farm settings, or null while loading.
 * Callers should treat null as "not yet known" and avoid rendering mode-dependent
 * UI until a non-null value is available.
 */
export function useSlicerMode(): SlicerMode | null {
  const { data, isLoading } = useFarmSettings();
  if (isLoading || data === undefined) return null;
  return data.slicerMode ?? 'Simple';
}
