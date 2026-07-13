import { useSystemCapabilities } from './useSystemCapabilities';

/**
 * Resolved per-tool maintenance eligibility. `enabled` is `true` unless the
 * backend explicitly reports `operatorFeatures.multiSlotFallbackEnabled: false`
 * — older API builds omit the flag entirely and we default to enabled per the
 * #725 tolerance contract. `loading` is only true when we don't yet know
 * either way (first fetch) and the caller may want to defer rendering
 * per-toolhead UI to avoid a flash of the wrong state.
 */
export interface PerToolMaintenanceGate {
  enabled: boolean;
  loading: boolean;
}

/**
 * Hook: is the per-toolhead maintenance surface (scoped alerts, scoped
 * deployments, per-tool analytics, odometer row, scope picker) allowed
 * on this backend? Reads `operatorFeatures.multiSlotFallbackEnabled`
 * from `GET /api/system/capabilities`. #711 (Dallas v2, tip 0bfa50343)
 * strips per-toolhead data from all these surfaces server-side when the
 * flag is off, so the UI must fall back to printer-wide.
 */
export function usePerToolMaintenanceEnabled(): PerToolMaintenanceGate {
  const { data, isLoading } = useSystemCapabilities();

  if (data === undefined) {
    return { enabled: true, loading: isLoading };
  }

  const flag = data.operatorFeatures?.multiSlotFallbackEnabled;
  return { enabled: flag !== false, loading: false };
}
