import { useState, useCallback, useMemo, type ReactNode } from 'react';
import { CollapsibleSection } from '@/common/components/ui/CollapsibleSection';
import { Badge } from '@/common/components/ui';

const STORAGE_KEY = 'pf.slicer.advancedDisclosure';

interface AdvancedSettingsDisclosureProps {
  children: ReactNode;
  /** Current settings snapshot */
  currentSettings: Record<string, unknown>;
  /** Original/default settings from profile for comparison */
  originalSettings: Record<string, unknown>;
}

function countOverrides(
  current: Record<string, unknown>,
  original: Record<string, unknown>,
): number {
  let count = 0;
  for (const key of Object.keys(current)) {
    const cur = current[key];
    const orig = original[key];
    if (cur === undefined || cur === null) continue;
    if (cur !== orig) count++;
  }
  return count;
}

/**
 * Wraps slicer settings panel in a collapsible Advanced disclosure.
 * Collapsed by default; state persists in localStorage.
 * Shows override count badge when parameters differ from profile defaults.
 */
export function AdvancedSettingsDisclosure({
  children,
  currentSettings,
  originalSettings,
}: AdvancedSettingsDisclosureProps) {
  const [expanded, setExpanded] = useState<boolean>(() => {
    try {
      const stored = localStorage.getItem(STORAGE_KEY);
      return stored === 'true';
    } catch {
      return false;
    }
  });

  const handleToggle = useCallback((next: boolean) => {
    setExpanded(next);
    try {
      localStorage.setItem(STORAGE_KEY, String(next));
    } catch { /* ignore */ }
  }, []);

  const overrideCount = useMemo(
    () => countOverrides(currentSettings, originalSettings),
    [currentSettings, originalSettings],
  );

  const collapsedTitle = overrideCount > 0
    ? `Advanced Settings (${overrideCount} override${overrideCount === 1 ? '' : 's'})`
    : 'Advanced Settings';

  return (
    <CollapsibleSection
      title="Advanced Settings"
      collapsedTitle={collapsedTitle}
      expanded={expanded}
      onToggle={handleToggle}
      defaultExpanded={false}
      headerActions={
        !expanded && overrideCount > 0 ? (
          <Badge variant="default" size="sm">
            {overrideCount}
          </Badge>
        ) : undefined
      }
    >
      {children}
    </CollapsibleSection>
  );
}
