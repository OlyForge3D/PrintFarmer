import { useEffect, useState, useCallback, useRef, useMemo } from 'react';
import { useLocation } from 'react-router';
import { useSlicer } from '@/hooks/useSlicer';
import { SettingsPagelet, SettingMetadata, SettingValue } from '@/common/components/SettingsPagelet';
import { SettingInputType } from '@/types/SettingInputType';
import { PageTemplate } from '@/common/components/PageTemplate';
import { SettingsIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import {
  fetchSettingsMetadata,
  fetchSettingsGroups,
  saveAllSettings,
  fetchSettingsUnified,
  SettingGroupMetadata,
} from '@/services/settingsApi';

/** Sidebar navigation item for settings sections */
interface NavItem {
  key: string;
  displayName: string;
  icon?: string;
  group?: string;
  order?: number;
}

export function SettingsPage() {
  const { isSlicerAvailable } = useSlicer();
  const [metadata, setMetadata] = useState<SettingMetadata[]>([]);
  const [groupMetadata, setGroupMetadata] = useState<SettingGroupMetadata[]>([]);
  const [settingsValues, setSettingsValues] = useState<Record<string, Record<string, unknown>>>({});
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [fieldErrorsBySection, setFieldErrorsBySection] = useState<Record<string, Record<string, string>>>({});
  const [activeSection, setActiveSection] = useState<string | null>(null);

  const location = useLocation();
  const sectionRefs = useRef<Record<string, HTMLDivElement | null>>({});
  const scrollContainerRef = useRef<HTMLDivElement | null>(null);

  const refetchSettingsValues = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [meta, groups] = await Promise.all([
        fetchSettingsMetadata(),
        fetchSettingsGroups(),
      ]);
      setMetadata(meta);
      setGroupMetadata(groups);
      const unified = await fetchSettingsUnified();
      const valueMap: Record<string, Record<string, unknown>> = {};
      for (const m of meta) {
        const sectionKey = m.key;
        valueMap[sectionKey] = (unified && typeof unified === 'object' && sectionKey in unified) ? (unified as Record<string, unknown>)[sectionKey] as Record<string, unknown> : {};
      }
      setSettingsValues(valueMap);
      setLoading(false);
    } catch (err) {
      setError('Failed to reload settings after save.');
      setLoading(false);
      console.error('Error in refetchSettingsValues:', err);
    }
  }, []);

  useEffect(() => {
    let mounted = true;
    setLoading(true);
    setError(null);
    Promise.all([fetchSettingsMetadata(), fetchSettingsGroups()])
      .then(async ([meta, groups]) => {
        if (!mounted) return;
        setMetadata(meta);
        setGroupMetadata(groups);
        await refetchSettingsValues();
        setLoading(false);
      })
      .catch(() => {
        setError('Failed to load settings metadata.');
        setLoading(false);
      });
    return () => { mounted = false; };
  }, [location, refetchSettingsValues]);

  // Scroll-spy: track which section is currently visible
  useEffect(() => {
    const container = scrollContainerRef.current;
    if (!container) return;

    // All section keys from metadata
    const allSectionKeys = metadata.map(m => m.key);

    const handleScroll = () => {
      const containerRect = container.getBoundingClientRect();
      
      // Find the section that is most visible in the viewport
      let closestSection: string | null = null;
      let closestDistance = Infinity;

      for (const key of allSectionKeys) {
        const el = sectionRefs.current[key];
        if (!el) continue;
        
        const rect = el.getBoundingClientRect();
        const relativeTop = rect.top - containerRect.top;
        
        // Calculate distance from top of viewport (prefer sections near the top)
        const distance = Math.abs(relativeTop - 100); // 100px offset for header
        
        if (relativeTop <= 150 && distance < closestDistance) {
          closestDistance = distance;
          closestSection = key;
        }
      }

      // If no section found near top, use the first one that's partially visible
      if (!closestSection) {
        for (const key of allSectionKeys) {
          const el = sectionRefs.current[key];
          if (!el) continue;
          const rect = el.getBoundingClientRect();
          if (rect.bottom > containerRect.top && rect.top < containerRect.bottom) {
            closestSection = key;
            break;
          }
        }
      }

      if (closestSection && closestSection !== activeSection) {
        setActiveSection(closestSection);
      }
    };

    container.addEventListener('scroll', handleScroll);
    // Initial check
    handleScroll();

    return () => container.removeEventListener('scroll', handleScroll);
  }, [metadata, activeSection]);

  // Build navigation items grouped and sorted
  const navItems: NavItem[] = metadata.map(m => ({
    key: m.key,
    displayName: m.displayName || m.className,
    icon: m.icon,
    group: m.group || 'Other',
    order: m.order ?? 999,
  })).sort((a, b) => (a.order ?? 999) - (b.order ?? 999));

  // Group nav items by group
  const groupedNavItems = navItems.reduce<Record<string, NavItem[]>>((acc, item) => {
    const group = item.group || 'Other';
    if (!acc[group]) acc[group] = [];
    acc[group].push(item);
    return acc;
  }, {});

  // Build a map of group key to order from group metadata
  const groupOrderMap = groupMetadata.reduce<Record<string, number>>((acc, g) => {
    acc[g.key] = g.order;
    return acc;
  }, {});

  // Sort groups by their order from group metadata, with fallback for undefined groups
  // Filter out Slicing group when slicer is not available
  const sortedGroups = useMemo(() => {
    return Object.keys(groupedNavItems)
      .filter(group => isSlicerAvailable || group !== 'Slicing')
      .sort((a, b) => {
        const orderA = groupOrderMap[a] ?? 999;
        const orderB = groupOrderMap[b] ?? 999;
        if (orderA !== orderB) return orderA - orderB;
        // If same order, sort alphabetically
        return a.localeCompare(b);
      });
  }, [groupedNavItems, groupOrderMap, isSlicerAvailable]);

  // Get display name for a group from metadata
  const getGroupDisplayName = (groupKey: string): string => {
    const group = groupMetadata.find(g => g.key === groupKey);
    return group?.displayName || groupKey;
  };

  const scrollToSection = (key: string) => {
    const el = sectionRefs.current[key];
    if (el) {
      el.scrollIntoView({ behavior: 'smooth', block: 'start' });
      setActiveSection(key);
    }
  };

  const handleFieldChange = (className: string, field: string, value: SettingValue) => {
    setSettingsValues(prev => ({
      ...prev,
      [className]: {
        ...(prev[className] || {}),
        [field]: value,
      }
    }));

    const metaForSection = metadata.find(m => m.key === className);
    if (metaForSection) {
      const sectionValues = {
        ...(settingsValues[className] || {}),
        [field]: value,
      };
      const errs = validateSection(metaForSection, sectionValues);
      setFieldErrorsBySection(prev => ({ ...prev, [className]: errs }));
    }
  };

  const handleSave = async () => {
    setSaving(true);
    setSaveError(null);
    try {
      const allErrors: Record<string, Record<string, string>> = {};
      for (const metaItem of metadata) {
        const sectionKey = metaItem.key;
        const vals = settingsValues[sectionKey] || {};
        const errs = validateSection(metaItem, vals);
        if (Object.keys(errs).length > 0) allErrors[sectionKey] = errs;
      }
      if (Object.keys(allErrors).length > 0) {
        setFieldErrorsBySection(allErrors);
        setSaveError('Fix validation errors before saving.');
        setSaving(false);
        return;
      }

      const payload: Record<string, unknown> = {};
      for (const meta of metadata) {
        const sectionKey = meta.key;
        payload[sectionKey] = settingsValues[sectionKey];
      }
      await saveAllSettings(payload);
      await refetchSettingsValues();
    } catch (err) {
      const maybe = err as unknown;
      if (typeof maybe === 'object' && maybe !== null) {
        const maybeObj = maybe as Record<string, unknown>;
        const resp = maybeObj['response'];
        if (resp && typeof resp === 'object') {
          const data = (resp as Record<string, unknown>)['data'];
          if (data && typeof data === 'object') {
            const errorsObj = (data as Record<string, unknown>)['errors'] as Record<string, unknown> | undefined;
            if (errorsObj && typeof errorsObj === 'object') {
              const newFieldErrors: Record<string, Record<string, string>> = {};
              for (const [key, msg] of Object.entries(errorsObj)) {
                const parts = key.split('.');
                let section = parts.length > 1 ? parts[0] : undefined;
                const fieldName = parts.length > 1 ? parts.slice(1).join('.') : parts[0];
                if (!section) {
                  const found = metadata.find(m => m.properties.some(p => p.name === fieldName));
                  section = found?.key;
                }
                if (section) {
                  newFieldErrors[section] = newFieldErrors[section] || {};
                  newFieldErrors[section][fieldName] = String(msg ?? 'Invalid value');
                } else {
                  setSaveError(String(msg ?? 'Failed to save settings.'));
                }
              }
              setFieldErrorsBySection(prev => ({ ...prev, ...newFieldErrors }));
            }
            if ('message' in (data as Record<string, unknown>) && !saveError) {
              setSaveError(String((data as Record<string, unknown>)['message'] ?? ''));
            }
          }
        }
      }
      if (!saveError) {
        setSaveError('Failed to save settings.');
      }
    } finally {
      setSaving(false);
    }
  };

  const validateSection = (metaItem: SettingMetadata, valuesObj: Record<string, unknown>): Record<string, string> => {
    const errs: Record<string, string> = {};
    for (const prop of metaItem.properties) {
      const val = valuesObj[prop.name];
      if (prop.attributes.includes('RequiredAttribute')) {
        const empty = val === undefined || val === null || val === '' || (Array.isArray(val) && val.length === 0);
        if (empty) { errs[prop.name] = 'This field is required.'; continue; }
      }
      const isNumberType = prop.display?.inputType === SettingInputType.Number || ['Number', 'int', 'double'].includes(prop.type);
      if (isNumberType) {
        const num = typeof val === 'number' ? val : (typeof val === 'string' && val !== '' ? Number(val) : NaN);
        if (!isNaN(num)) {
          if (typeof prop.display?.minValue === 'number' && num < prop.display!.minValue!) errs[prop.name] = `Minimum is ${prop.display!.minValue}`;
          if (typeof prop.display?.maxValue === 'number' && num > prop.display!.maxValue!) errs[prop.name] = `Maximum is ${prop.display!.maxValue}`;
        }
      }
    }
    return errs;
  };

  return (
    <PageTemplate
      title="Settings"
      subtitle="Configure PrintFarmer application settings"
      icon={SettingsIcon}
    >
      {loading ? (
        <div className="text-center text-pf-text-secondary">Loading settings...</div>
      ) : error ? (
        <div className="text-center text-pf-error">{error}</div>
      ) : (
        <div className="flex gap-6" style={{ height: 'calc(100vh - 160px)' }}>
          {/* Sidebar Navigation - Hidden on small screens */}
          <nav className="hidden lg:block w-56 shrink-0">
            <div className="pr-2">
              <div className="text-xs font-semibold text-pf-text-secondary uppercase tracking-wider mb-3">
                Sections
              </div>
              {sortedGroups.map(group => (
                <div key={group} className="mb-4">
                  <div className="text-xs font-medium text-pf-text-secondary mb-2 px-2">
                    {getGroupDisplayName(group)}
                  </div>
                  <ul className="space-y-0.5 ml-3">
                    {groupedNavItems[group].map(item => (
                      <li key={item.key}>
                        <Button
                          variant={activeSection === item.key ? 'tab' : 'subtle'}
                          type="button"
                          onClick={() => scrollToSection(item.key)}
                          className={`w-full justify-start px-3 py-1.5 text-sm
                            ${activeSection === item.key ? 'font-medium' : ''}`}
                          aria-current={activeSection === item.key ? 'true' : undefined}
                        >
                          {item.displayName}
                        </Button>
                      </li>
                    ))}
                  </ul>
                </div>
              ))}
            </div>
          </nav>

          {/* Main Content Area */}
          <div className="flex-1 min-w-0 flex flex-col overflow-hidden">
            <div 
              ref={scrollContainerRef}
              className="flex-1 space-y-6 overflow-y-auto scroll-smooth pr-2"
            >
              {/* Application Settings - rendered in same order as sidebar */}
              {sortedGroups.flatMap(group => 
                groupedNavItems[group].map(item => {
                  const meta = metadata.find(m => m.key === item.key);
                  if (!meta) return null;
                  return (
                    <div 
                      key={meta.key}
                      ref={el => { sectionRefs.current[meta.key] = el; }}
                      id={`section-${meta.key}`}
                    >
                      <SettingsPagelet
                        metadata={meta}
                        values={(settingsValues[meta.key] || {}) as Record<string, SettingValue>}
                        onChange={(field, value) => handleFieldChange(meta.key, field, value)}
                        fieldErrors={fieldErrorsBySection[meta.key]}
                      />
                    </div>
                  );
                })
              )}
            </div>
            {saveError && <div className="text-pf-error mb-2">{saveError}</div>}
            <div className="py-4 flex justify-end border-t border-pf-border mt-4">
              <Button
                type="button"
                onClick={handleSave}
                variant="primary"
                disabled={saving}
              >
                {saving ? 'Saving...' : 'Save All'}
              </Button>
            </div>
          </div>
        </div>
      )}
    </PageTemplate>
  );
}