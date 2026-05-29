import type { ReactNode } from 'react';
import { useFeatureFlag } from '@/common/hooks/useFeatureFlags';
import type { FeatureFlags } from '@/common/hooks/useFeatureFlags';

interface FeatureGateProps {
  /**
   * The feature flag key to check
   */
  flag: keyof FeatureFlags;
  
  /**
   * Content to render if the feature is enabled
   */
  children: ReactNode;
  
  /**
   * Optional fallback content to render if the feature is disabled
   */
  fallback?: ReactNode;
}

/**
 * Component that conditionally renders children based on feature flag state.
 * Shows children only if the flag is enabled. Shows fallback (or nothing) if disabled.
 * 
 * @example
 * ```tsx
 * <FeatureGate flag="orca.handcraftedEditors">
 *   <HandcraftedEditor />
 * </FeatureGate>
 * ```
 * 
 * @example
 * ```tsx
 * <FeatureGate flag="orca.schemaEditor" fallback={<p>Feature coming soon</p>}>
 *   <SchemaEditor />
 * </FeatureGate>
 * ```
 */
export function FeatureGate({ flag, children, fallback = null }: FeatureGateProps) {
  const isEnabled = useFeatureFlag(flag);
  
  if (!isEnabled) {
    return <>{fallback}</>;
  }
  
  return <>{children}</>;
}
