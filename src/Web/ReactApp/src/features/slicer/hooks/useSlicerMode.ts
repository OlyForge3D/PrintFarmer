import { useFarmSettings } from '@/features/settings/hooks/useFarmSettings';

export type SlicerMode = 'Simple' | 'Advanced';

/**
 * Returns the current slicer mode from farm settings.
 * Defaults to 'Simple' when the setting is absent.
 */
export function useSlicerMode(): SlicerMode {
  const { data } = useFarmSettings();
  return data?.slicerMode ?? 'Simple';
}
