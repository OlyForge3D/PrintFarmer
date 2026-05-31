import { useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router';
import { SettingsSearch } from '@/features/settings/components/SettingsSearch';
import { SettingsTabStrip } from '@/features/settings/components/SettingsTabStrip';
import { SETTINGS_TABS, DEFAULT_TAB } from '@/features/settings/types';

export const SettingsShell: React.FC = () => {
  const [searchParams, setSearchParams] = useSearchParams();

  const activeTab = searchParams.get('tab') || DEFAULT_TAB;
  const query = searchParams.get('q') || '';

  const handleTabChange = useCallback(
    (tabId: string) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        next.set('tab', tabId);
        return next;
      });
    },
    [setSearchParams]
  );

  const handleSearchChange = useCallback(
    (value: string) => {
      setSearchParams((prev) => {
        const next = new URLSearchParams(prev);
        if (value) {
          next.set('q', value);
        } else {
          next.delete('q');
        }
        return next;
      });
    },
    [setSearchParams]
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

  // If search narrows tabs and active tab is no longer visible, switch to first match
  const effectiveTab = useMemo(() => {
    if (!filteredTabIds || filteredTabIds.length === 0) return activeTab;
    if (filteredTabIds.includes(activeTab)) return activeTab;
    return filteredTabIds[0];
  }, [activeTab, filteredTabIds]);

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
        />
      )}
    </div>
  );
};
