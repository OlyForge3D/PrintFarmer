import { useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router';
import { SettingsSearch } from '@/features/settings/components/SettingsSearch';
import { SettingsTabStrip } from '@/features/settings/components/SettingsTabStrip';
import { SETTINGS_TABS, DEFAULT_TAB } from '@/features/settings/types';

export const SettingsShell: React.FC = () => {
  const [searchParams, setSearchParams] = useSearchParams();

  const activeTab = searchParams.get('tab') || DEFAULT_TAB;
  const query = searchParams.get('q') || '';
  const highlight = searchParams.get('highlight') || '';

  // Batched single-call updates — React Router v7 does not chain functional updaters.
  const handleTabChange = useCallback(
    (tabId: string) => {
      const next: Record<string, string> = { tab: tabId };
      if (query) next.q = query;
      if (highlight) next.highlight = highlight;
      setSearchParams(next);
    },
    [setSearchParams, query, highlight]
  );

  const handleSearchChange = useCallback(
    (value: string) => {
      const next: Record<string, string> = { tab: activeTab };
      if (value) next.q = value;
      if (highlight) next.highlight = highlight;
      setSearchParams(next);
    },
    [setSearchParams, activeTab, highlight]
  );

  const filteredTabIds = useMemo(() => {
    if (!query.trim()) return undefined;
    const lower = query.toLowerCase();
    return SETTINGS_TABS.filter(
      (tab) =>
        tab.label.toLowerCase().includes(lower) ||
        tab.keywords.some((kw) => kw.includes(lower))
    ).map((tab) => tab.id);
  }, [query]);

  // When a highlight keyword is present, prefer the owning tab over the default.
  const highlightTab = useMemo(() => {
    if (!highlight) return undefined;
    const lower = highlight.toLowerCase();
    return SETTINGS_TABS.find((tab) =>
      tab.keywords.some((kw) => kw.includes(lower))
    )?.id;
  }, [highlight]);

  // If search narrows tabs and active tab is no longer visible, switch to first match.
  // If only a highlight is set with no explicit tab param, jump to the tab that owns it.
  const effectiveTab = useMemo(() => {
    if (filteredTabIds) {
      if (filteredTabIds.length === 0) return activeTab;
      if (filteredTabIds.includes(activeTab)) return activeTab;
      return filteredTabIds[0];
    }
    if (highlightTab && !searchParams.has('tab')) return highlightTab;
    return activeTab;
  }, [activeTab, filteredTabIds, highlightTab, searchParams]);

  return (
    <div className="space-y-4">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
        <h1 className="text-xl font-semibold text-pf-text-primary">Settings</h1>
        <SettingsSearch value={query} onChange={handleSearchChange} />
      </div>

      {filteredTabIds && filteredTabIds.length === 0 ? (
        <div className="py-12 text-center text-pf-text-secondary">
          <p className="text-sm">No settings found matching &ldquo;{query}&rdquo;</p>
        </div>
      ) : (
        <SettingsTabStrip
          activeTab={effectiveTab}
          onTabChange={handleTabChange}
          filteredTabIds={filteredTabIds}
          highlight={highlight}
        />
      )}
    </div>
  );
};
