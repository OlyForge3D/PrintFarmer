import React, { useState, useRef, useEffect, useCallback, useMemo } from 'react';
import { createPortal } from 'react-dom';
import type { OrcaFilamentProfile } from '@/services/slicerProfilesService';

/** A custom (user) filament profile */
export interface CustomFilamentItem {
  id: string;
  name: string;
}

/** Filter state persisted via localStorage */
export interface FilamentFilterConfig {
  /** Manufacturer names to hide (empty = show all) */
  hiddenManufacturers: string[];
  /** Material types to hide (empty = show all) */
  hiddenMaterials: string[];
}

interface FilamentDropdownProps {
  /** System filament profiles from OrcaSlicer */
  profiles: OrcaFilamentProfile[];
  /** Custom user profiles */
  customProfiles: CustomFilamentItem[];
  /** Currently selected profile name */
  selectedProfileName: string;
  /** Called when a profile is selected */
  onSelect: (profileName: string, source: 'system' | 'custom') => void;
  /** Whether the dropdown is disabled */
  disabled?: boolean;
  /** Additional className for the trigger */
  className?: string;
  /** Filter config — which manufacturers/materials to hide */
  filterConfig: FilamentFilterConfig;
  /** Called when filter config changes */
  onFilterConfigChange: (config: FilamentFilterConfig) => void;
}

const FILTER_STORAGE_KEY = 'pf.slicer.filamentFilter';

/**
 * OrcaSlicer-style filament profile dropdown with expandable tree:
 * Manufacturer → Material Type → Individual profiles.
 * Includes search and a configure panel for filtering manufacturers/materials.
 */
/* eslint-disable local/pf-no-raw-html-controls -- Custom dropdown menu items need raw buttons */
export function FilamentProfileDropdown({
  profiles,
  customProfiles,
  selectedProfileName,
  onSelect,
  disabled = false,
  className = '',
  filterConfig,
  onFilterConfigChange,
}: FilamentDropdownProps) {
  const [isOpen, setIsOpen] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const [expandedMfrs, setExpandedMfrs] = useState<Set<string>>(new Set());
  const [showFilterPanel, setShowFilterPanel] = useState(false);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);

  const [menuPos, setMenuPos] = useState({ top: 0, left: 0, width: 0 });

  const updatePosition = useCallback(() => {
    if (triggerRef.current) {
      const rect = triggerRef.current.getBoundingClientRect();
      setMenuPos({
        top: rect.bottom + 2,
        left: rect.left,
        width: Math.max(rect.width, 320),
      });
    }
  }, []);

  useEffect(() => {
    if (isOpen) updatePosition();
  }, [isOpen, updatePosition]);

  // Close on click outside
  useEffect(() => {
    if (!isOpen) return;
    const handleClick = (e: MouseEvent) => {
      if (triggerRef.current?.contains(e.target as Node) || menuRef.current?.contains(e.target as Node)) return;
      setIsOpen(false);
    };
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, [isOpen]);

  // Close on Escape
  useEffect(() => {
    if (!isOpen) return;
    const handleKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setIsOpen(false); };
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  }, [isOpen]);

  // Build tree: manufacturer → material → profiles
  const { tree, allManufacturers, allMaterials } = useMemo(() => {
    const byMfr: Record<string, Record<string, OrcaFilamentProfile[]>> = {};
    const mfrSet = new Set<string>();
    const matSet = new Set<string>();

    for (const p of profiles) {
      const mfr = p.manufacturer || 'Generic';
      const mat = p.material || 'Other';
      mfrSet.add(mfr);
      matSet.add(mat);
      if (!byMfr[mfr]) byMfr[mfr] = {};
      if (!byMfr[mfr][mat]) byMfr[mfr][mat] = [];
      byMfr[mfr][mat].push(p);
    }

    return {
      tree: byMfr,
      allManufacturers: [...mfrSet].sort(),
      allMaterials: [...matSet].sort(),
    };
  }, [profiles]);

  // Apply filters
  const filteredTree = useMemo(() => {
    const result: Record<string, Record<string, OrcaFilamentProfile[]>> = {};
    const q = searchQuery.toLowerCase().trim();

    for (const mfr of Object.keys(tree)) {
      if (filterConfig.hiddenManufacturers.includes(mfr)) continue;
      const materials = tree[mfr];
      const filteredMats: Record<string, OrcaFilamentProfile[]> = {};

      for (const mat of Object.keys(materials)) {
        if (filterConfig.hiddenMaterials.includes(mat)) continue;
        let profs = materials[mat];
        if (q) {
          profs = profs.filter(p =>
            p.name.toLowerCase().includes(q) ||
            p.material.toLowerCase().includes(q) ||
            (p.manufacturer || '').toLowerCase().includes(q)
          );
        }
        if (profs.length > 0) filteredMats[mat] = profs;
      }

      if (Object.keys(filteredMats).length > 0) result[mfr] = filteredMats;
    }
    return result;
  }, [tree, filterConfig, searchQuery]);

  // Filtered custom profiles
  const filteredCustom = useMemo(() => {
    if (!searchQuery.trim()) return customProfiles;
    const q = searchQuery.toLowerCase();
    return customProfiles.filter(p => p.name.toLowerCase().includes(q));
  }, [customProfiles, searchQuery]);

  const toggleMfr = (mfr: string) => {
    setExpandedMfrs(prev => {
      const next = new Set(prev);
      if (next.has(mfr)) next.delete(mfr); else next.add(mfr);
      return next;
    });
  };

  const handleProfileClick = (name: string, source: 'system' | 'custom') => {
    onSelect(name, source);
    setIsOpen(false);
  };

  const toggleHiddenMfr = (mfr: string) => {
    const hidden = filterConfig.hiddenManufacturers.includes(mfr)
      ? filterConfig.hiddenManufacturers.filter(m => m !== mfr)
      : [...filterConfig.hiddenManufacturers, mfr];
    onFilterConfigChange({ ...filterConfig, hiddenManufacturers: hidden });
  };

  const toggleHiddenMat = (mat: string) => {
    const hidden = filterConfig.hiddenMaterials.includes(mat)
      ? filterConfig.hiddenMaterials.filter(m => m !== mat)
      : [...filterConfig.hiddenMaterials, mat];
    onFilterConfigChange({ ...filterConfig, hiddenMaterials: hidden });
  };

  // When searching, auto-expand everything; otherwise use manual toggle state
  const effectiveMfrs = useMemo(() => {
    if (searchQuery.trim()) return new Set(Object.keys(filteredTree));
    return expandedMfrs;
  }, [searchQuery, filteredTree, expandedMfrs]);

  const selectedProfile = profiles.find(p => p.name === selectedProfileName);
  const displayLabel = selectedProfile?.name || customProfiles.find(p => p.name === selectedProfileName)?.name || '';
  const totalFiltered = Object.values(filteredTree).reduce(
    (acc, mats) => acc + Object.values(mats).reduce((a, ps) => a + ps.length, 0), 0
  );

  const chevron = (expanded: boolean) => (
    <svg className={`w-3 h-3 shrink-0 transition-transform ${expanded ? 'rotate-90' : ''}`} fill="none" viewBox="0 0 24 24" stroke="currentColor">
      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
    </svg>
  );

  const checkmark = (
    <svg className="w-3 h-3 text-[#00a98f] shrink-0" fill="currentColor" viewBox="0 0 20 20">
      <path fillRule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z" clipRule="evenodd" />
    </svg>
  );

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        disabled={disabled}
        onClick={() => {
          setIsOpen(prev => {
            if (!prev) { setSearchQuery(''); setShowFilterPanel(false); }
            return !prev;
          });
        }}
        className={`flex items-center justify-between w-full px-3 py-2 text-sm text-left rounded-md border
          ${disabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer hover:border-pf-accent'}
          border-pf-border bg-pf-input text-pf-text-primary
          ${isOpen ? 'border-pf-accent ring-1 ring-pf-accent/30' : ''}
          ${className}`}
      >
        <span className={displayLabel ? '' : 'text-pf-text-muted'}>{displayLabel || '-- Select Filament --'}</span>
        <svg className={`w-4 h-4 ml-2 transition-transform ${isOpen ? 'rotate-180' : ''}`} fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
        </svg>
      </button>

      {isOpen && createPortal(
        <div
          ref={menuRef}
          className="fixed z-9999 rounded-md border border-[#3a3f48] shadow-xl overflow-hidden bg-[#2a3038] max-h-100"
          style={{ top: menuPos.top, left: menuPos.left, width: menuPos.width }}
        >
          {/* Header: search + filter toggle */}
          <div className="sticky top-0 bg-[#2a3038] border-b border-[#3a3f48] p-2 flex gap-1.5">
            <div className="relative flex-1">
              <svg className="absolute left-2 top-1/2 -translate-y-1/2 w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
              </svg>
              <input
                type="text"
                value={searchQuery}
                onChange={e => setSearchQuery(e.target.value)}
                placeholder="Search filaments..."
                autoFocus
                className="w-full pl-8 pr-3 py-1.5 text-sm bg-[#1e2228] text-white rounded border border-[#3a3f48]
                  focus:outline-none focus:border-[#00a98f] placeholder-gray-500"
              />
            </div>
            {/* Filter gear button */}
            <button
              type="button"
              onClick={() => setShowFilterPanel(prev => !prev)}
              className={`p-1.5 rounded border transition-colors ${showFilterPanel ? 'border-[#00a98f] bg-[#00a98f]/20 text-[#00a98f]' : 'border-[#3a3f48] text-gray-400 hover:text-white hover:border-gray-500'}`}
              title="Configure visible manufacturers & materials"
            >
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 4a1 1 0 011-1h16a1 1 0 011 1v2.586a1 1 0 01-.293.707l-6.414 6.414a1 1 0 00-.293.707V17l-4 4v-6.586a1 1 0 00-.293-.707L3.293 7.293A1 1 0 013 6.586V4z" />
              </svg>
            </button>
          </div>

          {/* Filter config panel */}
          {showFilterPanel && (
            <div className="border-b border-[#3a3f48] bg-[#1e2228] p-3 space-y-3 max-h-60 overflow-y-auto">
              {/* Manufacturers */}
              <div>
                <div className="text-xs font-medium text-gray-400 mb-1.5">Manufacturers</div>
                <div className="flex flex-wrap gap-1.5">
                  {allManufacturers.map(mfr => {
                    const hidden = filterConfig.hiddenManufacturers.includes(mfr);
                    return (
                      <button
                        key={mfr}
                        type="button"
                        onClick={() => toggleHiddenMfr(mfr)}
                        className={`px-2 py-0.5 text-xs rounded-full border transition-colors
                          ${hidden ? 'border-[#3a3f48] text-gray-500 bg-transparent' : 'border-[#00a98f] text-[#00a98f] bg-[#00a98f]/10'}`}
                      >
                        {mfr}
                      </button>
                    );
                  })}
                </div>
              </div>
              {/* Materials */}
              <div>
                <div className="text-xs font-medium text-gray-400 mb-1.5">Material Types</div>
                <div className="flex flex-wrap gap-1.5">
                  {allMaterials.map(mat => {
                    const hidden = filterConfig.hiddenMaterials.includes(mat);
                    return (
                      <button
                        key={mat}
                        type="button"
                        onClick={() => toggleHiddenMat(mat)}
                        className={`px-2 py-0.5 text-xs rounded-full border transition-colors
                          ${hidden ? 'border-[#3a3f48] text-gray-500 bg-transparent' : 'border-[#00a98f] text-[#00a98f] bg-[#00a98f]/10'}`}
                      >
                        {mat}
                      </button>
                    );
                  })}
                </div>
              </div>
            </div>
          )}

          {/* Scrollable tree content */}
          <div className="overflow-y-auto max-h-80">
            {/* Custom profiles section */}
            {filteredCustom.length > 0 && (
              <div>
                <div className="px-3 py-1.5 text-xs font-medium text-gray-400 border-b border-[#3a3f48] bg-[#252930]">
                  User Presets
                </div>
                {filteredCustom.map(p => (
                  <button
                    key={p.id}
                    type="button"
                    onClick={() => handleProfileClick(p.name, 'custom')}
                    className={`w-full text-left px-4 py-1.5 text-sm flex items-center gap-2 transition-colors
                      ${selectedProfileName === p.name ? 'text-[#00a98f] bg-[#00a98f]/10' : 'text-white hover:bg-[#353b44]'}`}
                  >
                    {selectedProfileName === p.name && checkmark}
                    <span className="truncate">★ {p.name}</span>
                  </button>
                ))}
              </div>
            )}

            {/* System profiles: Manufacturer → Material (click to select) */}
            {(Object.keys(filteredTree).length > 0 || filteredCustom.length > 0) && (
              <div className="px-3 py-1.5 text-xs font-medium text-gray-400 border-b border-[#3a3f48] bg-[#252930]">
                System Presets ({totalFiltered})
              </div>
            )}

            {Object.keys(filteredTree).length === 0 && filteredCustom.length === 0 && (
              <div className="px-3 py-4 text-sm text-gray-500 text-center">No matching profiles</div>
            )}

            {Object.keys(filteredTree).sort().map(mfr => {
              const materials = filteredTree[mfr];
              const mfrExpanded = effectiveMfrs.has(mfr);
              const mfrCount = Object.keys(materials).length;

              return (
                <div key={mfr}>
                  {/* Manufacturer row */}
                  <button
                    type="button"
                    onClick={() => toggleMfr(mfr)}
                    className="w-full text-left px-3 py-1.5 text-sm flex items-center gap-2 text-white hover:bg-[#353b44] font-medium"
                  >
                    {chevron(mfrExpanded)}
                    <span className="truncate">{mfr}</span>
                    <span className="text-xs text-gray-500 ml-auto">{mfrCount}</span>
                  </button>

                  {/* Material rows — click to select (picks the first profile for this manufacturer+material) */}
                  {mfrExpanded && Object.keys(materials).sort().map(mat => {
                    const matProfiles = materials[mat];
                    const representative = matProfiles[0];
                    const isSelected = matProfiles.some(p => p.name === selectedProfileName);

                    return (
                      <button
                        key={`${mfr}:${mat}`}
                        type="button"
                        onClick={() => handleProfileClick(representative.name, 'system')}
                        className={`w-full text-left pl-7 pr-3 py-1 text-sm flex items-center gap-2 transition-colors
                          ${isSelected ? 'text-[#00a98f] bg-[#00a98f]/10' : 'text-gray-300 hover:bg-[#353b44]'}`}
                      >
                        {isSelected && checkmark}
                        <span className="truncate">{mat}</span>
                        <span className="text-xs text-gray-500 ml-auto shrink-0">
                          {representative.nozzleTemperature ?? 210}°/{representative.bedTemperature ?? 60}°
                        </span>
                      </button>
                    );
                  })}
                </div>
              );
            })}
          </div>
        </div>,
        document.body,
      )}
    </>
  );
}

export { FILTER_STORAGE_KEY };
export default FilamentProfileDropdown;
