import React, { useState, useRef, useEffect, useCallback } from 'react';
import { createPortal } from 'react-dom';

/** A leaf item the user can select */
export interface CascadingMenuItem {
  id: string;
  label: string;
  /** Optional secondary text (e.g. temperature info) */
  detail?: string;
  /** Whether this item is currently selected */
  selected?: boolean;
}

/** A group that expands into a submenu on hover */
export interface CascadingMenuGroup {
  id: string;
  label: string;
  children: CascadingMenuItem[];
}

/** A section with a header label containing either flat items or groups */
export interface CascadingMenuSection {
  label: string;
  /** Flat selectable items (e.g., user presets) */
  items?: CascadingMenuItem[];
  /** Expandable groups with submenus (e.g., manufacturer → materials) */
  groups?: CascadingMenuGroup[];
}

interface CascadingMenuDropdownProps {
  /** The display text for the trigger button */
  triggerLabel: string;
  /** Sections to render in the dropdown */
  sections: CascadingMenuSection[];
  /** Called when a leaf item is selected */
  onSelect: (itemId: string, sectionLabel: string) => void;
  /** Whether the trigger is disabled */
  disabled?: boolean;
  /** Additional className for the trigger */
  className?: string;
  /** Placeholder when nothing selected */
  placeholder?: string;
}

/**
 * OrcaSlicer-style cascading dropdown menu with hover-expandable subgroups.
 * Renders via portal to avoid z-index/overflow clipping issues.
 */
/* eslint-disable local/pf-no-raw-html-controls -- Custom dropdown menu items need raw buttons for proper hover/menu behavior */
export function CascadingMenuDropdown({
  triggerLabel,
  sections,
  onSelect,
  disabled = false,
  className = '',
  placeholder = '-- Select --',
}: CascadingMenuDropdownProps) {
  const [isOpen, setIsOpen] = useState(false);
  const [hoveredGroupId, setHoveredGroupId] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const triggerRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const submenuRef = useRef<HTMLDivElement>(null);
  const hoverTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Position the menu below the trigger
  const [menuPos, setMenuPos] = useState({ top: 0, left: 0, width: 0 });
  const [submenuPos, setSubmenuPos] = useState({ top: 0, left: 0 });

  const updatePosition = useCallback(() => {
    if (triggerRef.current) {
      const rect = triggerRef.current.getBoundingClientRect();
      setMenuPos({
        top: rect.bottom + 2,
        left: rect.left,
        width: Math.max(rect.width, 280),
      });
    }
  }, []);

  useEffect(() => {
    if (isOpen) {
      updatePosition();
    }
  }, [isOpen, updatePosition]);

  // Close on click outside
  useEffect(() => {
    if (!isOpen) return;
    const handleClick = (e: MouseEvent) => {
      const target = e.target as Node;
      if (
        triggerRef.current?.contains(target) ||
        menuRef.current?.contains(target) ||
        submenuRef.current?.contains(target)
      ) return;
      setIsOpen(false);
    };
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, [isOpen]);

  // Close on Escape
  useEffect(() => {
    if (!isOpen) return;
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setIsOpen(false);
    };
    document.addEventListener('keydown', handleKey);
    return () => document.removeEventListener('keydown', handleKey);
  }, [isOpen]);

  const handleGroupHover = (groupId: string) => {
    if (hoverTimeoutRef.current) clearTimeout(hoverTimeoutRef.current);
    hoverTimeoutRef.current = setTimeout(() => {
      setHoveredGroupId(groupId);
      // Compute submenu position from menu ref
      if (menuRef.current) {
        const menuRect = menuRef.current.getBoundingClientRect();
        setSubmenuPos({ top: menuRect.top, left: menuRect.right + 2 });
      }
    }, 100);
  };

  const handleGroupLeave = () => {
    if (hoverTimeoutRef.current) clearTimeout(hoverTimeoutRef.current);
    hoverTimeoutRef.current = setTimeout(() => setHoveredGroupId(null), 200);
  };

  const handleSubmenuEnter = () => {
    if (hoverTimeoutRef.current) clearTimeout(hoverTimeoutRef.current);
  };

  const handleItemClick = (itemId: string, sectionLabel: string) => {
    onSelect(itemId, sectionLabel);
    setIsOpen(false);
  };

  // Filter sections by search query
  const filteredSections = searchQuery.trim()
    ? sections.map(section => {
        const q = searchQuery.toLowerCase();
        const filteredItems = section.items?.filter(
          item => item.label.toLowerCase().includes(q) || item.detail?.toLowerCase().includes(q)
        );
        const filteredGroups = section.groups?.map(group => ({
          ...group,
          children: group.children.filter(
            child => child.label.toLowerCase().includes(q) || child.detail?.toLowerCase().includes(q)
          ),
        })).filter(g => g.children.length > 0);

        return { ...section, items: filteredItems, groups: filteredGroups };
      }).filter(s => (s.items?.length ?? 0) > 0 || (s.groups?.length ?? 0) > 0)
    : sections;

  const hoveredGroup = filteredSections
    .flatMap(s => s.groups ?? [])
    .find(g => g.id === hoveredGroupId);

  const displayLabel = triggerLabel || placeholder;

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        disabled={disabled}
        onClick={() => {
          setIsOpen(prev => {
            if (!prev) {
              setSearchQuery('');
              setHoveredGroupId(null);
            }
            return !prev;
          });
        }}
        className={`flex items-center justify-between w-full px-3 py-2 text-sm text-left rounded-md border
          ${disabled ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer hover:border-pf-accent'}
          border-pf-border bg-pf-input text-pf-text-primary
          ${isOpen ? 'border-pf-accent ring-1 ring-pf-accent/30' : ''}
          ${className}`}
      >
        <span className={triggerLabel ? '' : 'text-pf-text-muted'}>{displayLabel}</span>
        <svg className={`w-4 h-4 ml-2 transition-transform ${isOpen ? 'rotate-180' : ''}`} fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
        </svg>
      </button>

      {isOpen && createPortal(
        <>
          {/* Main dropdown menu */}
          <div
            ref={menuRef}
            className="fixed z-9999 rounded-md border border-[#3a3f48] shadow-xl overflow-hidden bg-[#2a3038] max-h-100"
            style={{ top: menuPos.top, left: menuPos.left, width: menuPos.width }}
          >
            {/* Search input */}
            <div className="sticky top-0 bg-[#2a3038] border-b border-[#3a3f48] p-2">
              <div className="relative">
                <svg className="absolute left-2 top-1/2 -translate-y-1/2 w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                </svg>
                <input
                  type="text"
                  value={searchQuery}
                  onChange={e => setSearchQuery(e.target.value)}
                  placeholder="Search..."
                  autoFocus
                  className="w-full pl-8 pr-3 py-1.5 text-sm bg-[#1e2228] text-white rounded border border-[#3a3f48] 
                    focus:outline-none focus:border-[#00a98f] placeholder-gray-500"
                />
              </div>
            </div>

            {/* Scrollable content */}
            <div className="overflow-y-auto max-h-85">
              {filteredSections.length === 0 && (
                <div className="px-3 py-4 text-sm text-gray-500 text-center">No matching profiles</div>
              )}

              {filteredSections.map((section, sIdx) => (
                <div key={sIdx}>
                  {/* Section header */}
                  <div className="px-3 py-1.5 text-xs font-medium text-gray-400 border-b border-[#3a3f48] bg-[#252930]">
                    {section.label}
                  </div>

                  {/* Flat items */}
                  {section.items?.map(item => (
                    <button
                      key={item.id}
                      type="button"
                      onClick={() => handleItemClick(item.id, section.label)}
                      className={`w-full text-left px-3 py-1.5 text-sm flex items-center gap-2 transition-colors
                        ${item.selected ? 'text-[#00a98f] bg-[#00a98f]/10' : 'text-white hover:bg-[#353b44]'}`}
                    >
                      {item.selected && (
                        <svg className="w-3 h-3 text-[#00a98f] shrink-0" fill="currentColor" viewBox="0 0 20 20">
                          <path fillRule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z" clipRule="evenodd" />
                        </svg>
                      )}
                      <span className="truncate">{item.label}</span>
                      {item.detail && <span className="text-xs text-gray-500 ml-auto shrink-0">{item.detail}</span>}
                    </button>
                  ))}

                  {/* Groups with hover submenus */}
                  {section.groups?.map(group => (
                    <button
                      key={group.id}
                      type="button"
                      onMouseEnter={() => handleGroupHover(group.id)}
                      onMouseLeave={handleGroupLeave}
                      className={`w-full text-left px-3 py-1.5 text-sm flex items-center justify-between transition-colors
                        ${hoveredGroupId === group.id ? 'bg-[#00a98f]/20 text-[#00a98f]' : 'text-white hover:bg-[#353b44]'}`}
                    >
                      <span className="truncate">{group.label}</span>
                      <svg className="w-3 h-3 shrink-0 ml-2" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
                      </svg>
                    </button>
                  ))}
                </div>
              ))}
            </div>
          </div>

          {/* Submenu portal */}
          {hoveredGroup && hoveredGroup.children.length > 0 && (
            <div
              ref={submenuRef}
              onMouseEnter={handleSubmenuEnter}
              onMouseLeave={handleGroupLeave}
              className="fixed z-10000 rounded-md border border-[#3a3f48] shadow-xl overflow-y-auto min-w-50 max-w-75 max-h-87.5 bg-[#2a3038]"
              style={submenuPos}
            >
              {hoveredGroup.children.map(child => (
                <button
                  key={child.id}
                  type="button"
                  onClick={() => handleItemClick(child.id, hoveredGroup.label)}
                  className={`w-full text-left px-3 py-1.5 text-sm flex items-center gap-2 transition-colors
                    ${child.selected ? 'text-[#00a98f] bg-[#00a98f]/10' : 'text-white hover:bg-[#353b44]'}`}
                >
                  {child.selected && (
                    <svg className="w-3 h-3 text-[#00a98f] shrink-0" fill="currentColor" viewBox="0 0 20 20">
                      <path fillRule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z" clipRule="evenodd" />
                    </svg>
                  )}
                  <span className="truncate">{child.label}</span>
                  {child.detail && <span className="text-xs text-gray-500 ml-auto shrink-0">{child.detail}</span>}
                </button>
              ))}
            </div>
          )}
        </>,
        document.body,
      )}
    </>
  );
}

export default CascadingMenuDropdown;
