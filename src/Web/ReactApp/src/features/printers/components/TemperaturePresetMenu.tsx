import { useEffect, useId, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Button } from '@/common/components/ui';
import { ChevronDownIcon } from '@/common/components/icons/MdiIcons';
import { materialPresets } from '@/features/printers/constants/temperaturePresets';

interface TemperaturePresetMenuProps {
  disabled: boolean;
  onApplyPreset: (preset: string) => void;
  onApplySingleHeaterPreset: (heater: 'hotend' | 'bed', preset: string) => void;
}

const GROUPS = [
  { label: 'Both heaters', heater: undefined },
  { label: 'Hotend', heater: 'hotend' },
  { label: 'Bed', heater: 'bed' },
] as const;
const MENU_MARGIN = 8;
const MENU_GAP = 4;
const MENU_MAX_HEIGHT = 240;
const MENU_WIDTH = 224;

function focusPresetItem(menu: HTMLDivElement | null, item: HTMLButtonElement | undefined) {
  if (!menu || !item) return;
  item.focus({ preventScroll: true });
  if (item.offsetTop < menu.scrollTop) menu.scrollTop = item.offsetTop;
  if (item.offsetTop + item.offsetHeight > menu.scrollTop + menu.clientHeight) {
    menu.scrollTop = item.offsetTop + item.offsetHeight - menu.clientHeight;
  }
}

export function TemperaturePresetMenu({ disabled, onApplyPreset, onApplySingleHeaterPreset }: TemperaturePresetMenuProps) {
  const id = useId();
  const trigger = useRef<HTMLButtonElement>(null);
  const panel = useRef<HTMLDivElement>(null);
  const focusLast = useRef(false);
  const [position, setPosition] = useState<{ left: number; top: number; width: number; maxHeight: number } | null>(null);
  if (disabled && position) setPosition(null);

  const close = () => {
    setPosition(null);
    trigger.current?.focus();
  };
  const open = (last = false) => {
    if (disabled || !trigger.current) return;
    const bounds = trigger.current.getBoundingClientRect();
    const below = window.innerHeight - bounds.bottom - MENU_GAP - MENU_MARGIN;
    const above = bounds.top - MENU_GAP - MENU_MARGIN;
    const opensBelow = below >= Math.min(MENU_MAX_HEIGHT, above);
    const maxHeight = Math.max(0, Math.min(MENU_MAX_HEIGHT, opensBelow ? below : above));
    const width = Math.min(MENU_WIDTH, window.innerWidth - MENU_MARGIN * 2);
    focusLast.current = last;
    setPosition({
      left: Math.max(MENU_MARGIN, Math.min(bounds.right - width, window.innerWidth - width - MENU_MARGIN)),
      top: Math.max(MENU_MARGIN, opensBelow ? bounds.bottom + MENU_GAP : bounds.top - MENU_GAP - maxHeight),
      width, maxHeight,
    });
  };

  useEffect(() => {
    if (!position) return;
    const items = panel.current?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]');
    const initialItem = items?.[focusLast.current ? items.length - 1 : 0];
    focusPresetItem(panel.current, initialItem);
    const anchorTop = trigger.current?.getBoundingClientRect().top;
    const outside = (event: PointerEvent) => {
      if (event.target instanceof Node && !panel.current?.contains(event.target) && !trigger.current?.contains(event.target)) {
        setPosition(null);
      }
    };
    const reposition = (event: Event) => {
      if (event.target instanceof Node && panel.current?.contains(event.target)) return;
      if (event.type === 'resize' || trigger.current?.getBoundingClientRect().top !== anchorTop) setPosition(null);
    };
    document.addEventListener('pointerdown', outside);
    window.addEventListener('resize', reposition);
    window.addEventListener('scroll', reposition, true);
    return () => {
      document.removeEventListener('pointerdown', outside);
      window.removeEventListener('resize', reposition);
      window.removeEventListener('scroll', reposition, true);
    };
  }, [position]);

  return <>
    <Button ref={trigger} type="button" variant="secondary" size="sm" disabled={disabled}
      aria-label="Apply temperature preset" aria-haspopup="menu" aria-expanded={Boolean(position)}
      aria-controls={position ? id : undefined} className="h-8! min-h-8! gap-1 py-0! text-xs!"
      iconRight={<ChevronDownIcon className="size-3.5" />}
      onClick={() => position ? close() : open()}
      onKeyDown={event => {
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
          event.preventDefault();
          open(event.key === 'ArrowUp');
        }
      }}>
      Presets
    </Button>
    {position && createPortal(
      <div ref={panel} id={id} role="menu" aria-label="Temperature presets" style={position}
        className="fixed z-[100] overflow-y-auto overscroll-contain rounded-md border border-pf-border bg-pf-bg-1 py-1 text-pf-text-primary shadow-lg"
        onKeyDown={event => {
          event.stopPropagation();
          if (event.key === 'Escape') { event.preventDefault(); close(); return; }
          if (event.key === 'Tab') { close(); return; }
          const items = Array.from(panel.current?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]') ?? []);
          const current = items.findIndex(item => item === document.activeElement);
          let next: number | undefined;
          if (event.key === 'ArrowDown') next = (current + 1) % items.length;
          if (event.key === 'ArrowUp') next = (current - 1 + items.length) % items.length;
          if (event.key === 'Home') next = 0;
          if (event.key === 'End') next = items.length - 1;
          if (event.key.length === 1 && /\S/.test(event.key)) {
            next = items.findIndex((_, offset) => items[(current + 1 + offset) % items.length]?.dataset.material?.toLowerCase().startsWith(event.key.toLowerCase()));
            if (next >= 0) next = (current + 1 + next) % items.length;
          }
          if (next !== undefined && next >= 0) {
            event.preventDefault();
            focusPresetItem(panel.current, items[next]);
          }
        }}>
        {GROUPS.map(group => <div key={group.label} role="group" aria-label={group.label}>
          <div aria-hidden="true" className="px-3 py-1 text-xs font-normal text-pf-text-secondary">{group.label}</div>
          {materialPresets.map(preset => {
            const value = group.heater ? `${group.heater}:${preset.value}` : preset.value;
            const targets = group.heater ? `${preset[group.heater]}°C` : `${preset.hotend} / ${preset.bed}°C`;
            return <Button key={value} type="button" role="menuitem" tabIndex={-1} variant="ghost" size="sm"
              data-temperature-preset={value} data-material={preset.label} aria-label={`${group.label} ${preset.label}, ${targets}`}
              className="h-7! min-h-7! w-full rounded-none px-3 py-1.5 text-left text-xs! font-normal [&>span]:flex [&>span]:w-full [&>span]:items-center [&>span]:justify-between [&>span]:gap-2"
              onClick={() => {
                close();
                if (disabled) return;
                if (group.heater) onApplySingleHeaterPreset(group.heater, preset.value);
                else onApplyPreset(preset.value);
              }}>
              <span>{preset.label}</span><span className="text-pf-text-secondary">{targets}</span>
            </Button>;
          })}
        </div>)}
      </div>, document.body)}
  </>;
}
