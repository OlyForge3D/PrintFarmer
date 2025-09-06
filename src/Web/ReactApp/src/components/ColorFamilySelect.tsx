import { useState, useRef, useEffect } from 'react';
import { colorFamilyBgClass } from '@/utils/colorFamilies';

interface Props {
  value: string;
  onChange: (val: string) => void;
  options: string[]; // color family names
  placeholder?: string;
  label?: string;
  id?: string;
}

// Accessible custom select (button + listbox) with per-option color swatch.
export function ColorFamilySelect({ value, onChange, options, placeholder = 'All Colors', label = 'Filter by color family', id }: Props) {
  const [open, setOpen] = useState(false);
  const [activeIdx, setActiveIdx] = useState(-1);
  const btnRef = useRef<HTMLButtonElement | null>(null);
  const listRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    const handleDoc = (e: MouseEvent) => {
      if (!open) return;
      if (btnRef.current?.contains(e.target as Node)) return;
      if (listRef.current?.contains(e.target as Node)) return;
      setOpen(false);
    };
    const handleEsc = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', handleDoc);
    document.addEventListener('keydown', handleEsc);
    return () => { document.removeEventListener('mousedown', handleDoc); document.removeEventListener('keydown', handleEsc); };
  }, [open]);

  const families = options; // already unique & sorted upstream
  const visibleLabel = value || placeholder;

  const commit = (val: string) => {
    onChange(val);
    setOpen(false);
    btnRef.current?.focus();
  };

  const move = (delta: number) => {
    setActiveIdx(prev => {
      const total = families.length + 1; // +1 for "All"
      let next = prev + delta;
      if (next < 0) next = total - 1;
      if (next >= total) next = 0;
      return next;
    });
  };

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (!open) {
      if (['ArrowDown', 'Enter', ' '].includes(e.key)) {
        e.preventDefault();
        setOpen(true);
        setActiveIdx(0);
      }
      return;
    }
    if (e.key === 'ArrowDown') { e.preventDefault(); move(1); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); move(-1); }
    else if (e.key === 'Home') { e.preventDefault(); setActiveIdx(0); }
    else if (e.key === 'End') { e.preventDefault(); setActiveIdx(families.length); }
    else if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      if (activeIdx === 0) commit('');
      else if (activeIdx > 0) commit(families[activeIdx - 1]);
    }
  };

  return (
    <div className="relative" aria-label={label} id={id ? id + '-wrapper' : undefined}>
      <button
        ref={btnRef}
        type="button"
        aria-haspopup="listbox"
        data-open={open ? 'true' : 'false'}
        onClick={() => { setOpen(o => !o); if (!open) setActiveIdx( value ? (families.indexOf(value) + 1) : 0 ); }}
        onKeyDown={onKeyDown}
        className="flex items-center gap-2 px-3 py-2 bg-pf-bg-0 border border-pf-border rounded text-pf-text-primary text-sm hover:bg-pf-bg-2 focus:outline-none focus:ring-2 focus:ring-blue-600"
      >
        {value ? (
          <span className={`w-4 h-4 rounded border border-pf-border ${colorFamilyBgClass[value] || 'bg-gray-500'}`} aria-hidden="true" />
        ) : (
          <span className="w-4 h-4 rounded border border-dashed border-pf-border" aria-hidden="true" />
        )}
        <span className="truncate max-w-[6rem]">{visibleLabel}</span>
        <span className="text-xs opacity-60" aria-hidden="true">{open ? '▲' : '▼'}</span>
      </button>
      {open && (
        <div
          ref={listRef}
          role="listbox"
          tabIndex={-1}
          aria-label={label}
          aria-activedescendant={activeIdx >= 0 ? `${id || 'color-family'}-opt-${activeIdx}` : undefined}
          className="absolute z-50 mt-1 w-52 max-h-72 overflow-auto rounded border border-pf-border bg-pf-bg-1 shadow-lg py-1"
          onKeyDown={onKeyDown}
        >
          <div
            role="option"
            id={`${id || 'color-family'}-opt-0`}
            className={`flex items-center gap-2 px-2 py-1 text-sm cursor-pointer select-none ${activeIdx === 0 ? 'bg-blue-600 text-white' : 'hover:bg-pf-bg-2'} ${value === '' ? 'font-medium' : ''}`}
            onMouseEnter={() => setActiveIdx(0)}
            onMouseDown={e => e.preventDefault()}
            onClick={() => commit('')}
            data-active={activeIdx === 0 ? 'true' : 'false'}
          >
            <span className="w-4 h-4 rounded border border-dashed border-pf-border" aria-hidden="true" />
            All Colors
          </div>
          {families.map((fam, i) => {
            const idx = i + 1; // shift for All
            const selected = value === fam;
            const active = activeIdx === idx;
            return (
              <div
                key={fam}
                role="option"
                id={`${id || 'color-family'}-opt-${idx}`}
                className={`flex items-center gap-2 px-2 py-1 text-sm cursor-pointer select-none ${active ? 'bg-blue-600 text-white' : 'hover:bg-pf-bg-2'} ${selected && !active ? 'font-medium' : ''}`}
                onMouseEnter={() => setActiveIdx(idx)}
                onMouseDown={e => e.preventDefault()}
                onClick={() => commit(fam)}
                data-active={active ? 'true' : 'false'}
              >
                <span className={`w-4 h-4 rounded border border-pf-border ${colorFamilyBgClass[fam] || 'bg-gray-500'}`} aria-hidden="true" />
                <span>{fam}</span>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
