import { act, fireEvent, render, screen, within } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { FilamentSlots } from '../FilamentSlots';
import {
  formatGrams,
  filamentDisplayName,
  type FilamentSlotView,
  type SlotAction,
  type SlotPanelContent,
} from '../filamentSlotModel';

const slot = (overrides: Partial<FilamentSlotView> & { key: string; id: string }): FilamentSlotView => ({
  stock: 'assigned',
  material: 'PLA',
  displayName: 'PLA Black',
  color: '#111111',
  grams: 340,
  spoolId: 12,
  ...overrides,
});

const qidiRail = [
  slot({ key: 'a', id: '1A', active: true, testId: 'gate-a' }),
  slot({ key: 'b', id: '1B', stock: 'depleted', grams: 0, displayName: 'PETG White', material: 'PETG', testId: 'gate-b' }),
  slot({ key: 'c', id: '1C', stock: 'none', spoolId: undefined, displayName: undefined, material: undefined, grams: undefined, color: undefined, testId: 'gate-c' }),
  slot({ key: 'd', id: '1D', testId: 'gate-d' }),
];

const noop = () => {};
const qidiActions = (s: FilamentSlotView): SlotAction[] => [
  ...(s.stock === 'none'
    ? [{ kind: 'assign' as const, label: `Assign spool to slot ${s.id}`, onClick: noop }]
    : [
      { kind: 'change' as const, label: `Change spool in slot ${s.id}`, onClick: noop },
      { kind: 'unassign' as const, label: `Unassign spool from slot ${s.id}`, onClick: noop },
    ]),
  { kind: 'load', label: `Load slot ${s.id}`, onClick: noop },
  { kind: 'unload', label: `Unload slot ${s.id}`, onClick: noop },
  { kind: 'eject', label: `Eject slot ${s.id}`, onClick: noop },
];

function Harness({
  slots,
  summarySlot,
  mode = 'multi',
  unitWord = 'slots',
  panelFor = (s) => ({ title: `Slot ${s.id}`, actions: qidiActions(s) }),
}: {
  slots: FilamentSlotView[];
  summarySlot: FilamentSlotView | null;
  mode?: 'single' | 'multi';
  unitWord?: 'slots' | 'tools';
  panelFor?: (s: FilamentSlotView) => SlotPanelContent;
}) {
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  return (
    <FilamentSlots
      slots={slots}
      summarySlot={summarySlot}
      mode={mode}
      unitWord={unitWord}
      selectedKey={selectedKey}
      onSelect={setSelectedKey}
      panelFor={panelFor}
    />
  );
}

describe('FilamentSlots (Option 4)', () => {
  describe('resting line', () => {
    it('summarises the active feed on one line: id · name · grams', () => {
      render(<Harness slots={qidiRail} summarySlot={qidiRail[0]} />);

      const line = screen.getByTestId('filament-summary-line');
      expect(line).toHaveTextContent(/^1A·PLA Black·340 g$/);
      expect(screen.getByTestId('filament-summary')).toHaveAccessibleName('1A: PLA Black, 340 g, in use, 1 empty, Show 4 slots');
      expect(screen.queryByTestId('filament-summary-mini')).not.toBeInTheDocument();
      // The rail stays collapsed until the operator asks for it.
      expect(screen.getByTestId('gate-a')).not.toBeVisible();
    });

    it('omits grams when the remaining weight is unknown instead of showing 0', () => {
      const unknown = slot({ key: 'a', id: 'T0', active: true, grams: undefined });
      render(<Harness slots={[unknown, slot({ key: 'b', id: 'T1' })]} summarySlot={unknown} unitWord="tools" />);

      const line = screen.getByTestId('filament-summary-line');
      expect(line).toHaveTextContent(/^T0·PLA Black$/);
      expect(within(line).queryByTestId('filament-grams')).not.toBeInTheDocument();
      expect(line).not.toHaveTextContent(/0 g/);
    });

    it('falls back to the mini spool rail when no active feed is known', () => {
      render(<Harness slots={qidiRail} summarySlot={null} />);

      expect(screen.queryByTestId('filament-summary-line')).not.toBeInTheDocument();
      const mini = screen.getByTestId('filament-summary-mini');
      expect(mini.querySelectorAll('[data-stock]')).toHaveLength(4);
      expect(screen.getByTestId('filament-summary')).toHaveAccessibleName('Filament slots, 1 empty, Show 4 slots');
    });

    it('keeps the full name in the accessible name when the visible name truncates', () => {
      const long = slot({ key: 'a', id: '1A', active: true, displayName: 'PLA Silk Rainbow Gradient Extra Long Name' });
      render(<Harness slots={[long, qidiRail[1]]} summarySlot={long} />);

      expect(screen.getByTestId('filament-summary-name')).toHaveClass('truncate');
      expect(screen.getByTestId('filament-summary')).toHaveAccessibleName(/PLA Silk Rainbow Gradient Extra Long Name/);
    });
  });

  describe('disclosure', () => {
    it('wires aria-expanded/aria-controls to the rail and says how many slots it shows', () => {
      render(<Harness slots={qidiRail} summarySlot={qidiRail[0]} />);

      const summary = screen.getByTestId('filament-summary');
      expect(summary).toHaveAttribute('aria-expanded', 'false');
      expect(summary).toHaveAccessibleName(/Show 4 slots/);
      const rail = document.getElementById(summary.getAttribute('aria-controls')!);
      expect(rail).not.toBeVisible();

      fireEvent.click(summary);
      expect(summary).toHaveAttribute('aria-expanded', 'true');
      expect(summary).toHaveAccessibleName(/Hide 4 slots/);
      expect(rail).toBeVisible();
      expect(within(rail!).getByRole('group', { name: 'Filament slots' })).toBeInTheDocument();
    });

    it('shows the amber dot only when a slot is depleted', () => {
      const { unmount } = render(<Harness slots={qidiRail} summarySlot={qidiRail[0]} />);
      expect(screen.getByTestId('filament-empty-dot')).toBeInTheDocument();
      expect(screen.getByTestId('filament-summary')).toHaveAccessibleName(/1 empty/);
      unmount();

      const healthy = [qidiRail[0], qidiRail[2], qidiRail[3]];
      render(<Harness slots={healthy} summarySlot={healthy[0]} />);
      expect(screen.queryByTestId('filament-empty-dot')).not.toBeInTheDocument();
    });
  });

  describe('rail', () => {
    it('distinguishes empty (depleted) from no spool assigned', () => {
      render(<Harness slots={qidiRail} summarySlot={qidiRail[0]} />);

      const depleted = screen.getByTestId('gate-b');
      expect(depleted).toHaveAttribute('data-state', 'depleted');
      expect(within(depleted).getByTestId('slot-badge-depleted')).toBeInTheDocument();
      expect(within(depleted).queryByTestId('slot-badge-assign')).not.toBeInTheDocument();
      expect(depleted).toHaveTextContent('Empty');
      expect(depleted).toHaveAccessibleName('1B: PETG White, empty');

      const none = screen.getByTestId('gate-c');
      expect(none).toHaveAttribute('data-state', 'none');
      expect(within(none).getByTestId('slot-badge-assign')).toBeInTheDocument();
      expect(within(none).queryByTestId('slot-badge-depleted')).not.toBeInTheDocument();
      expect(none).toHaveAccessibleName('1C: Assign spool');
      expect(none).not.toHaveTextContent(/no spool/i);
    });

    it('keeps the active feed and the selected slot visually distinct', () => {
      render(<Harness slots={qidiRail} summarySlot={qidiRail[0]} />);
      fireEvent.click(screen.getByTestId('gate-d'));

      const active = screen.getByTestId('gate-a');
      const selected = screen.getByTestId('gate-d');
      expect(active).toHaveAttribute('aria-current', 'true');
      expect(active).toHaveAttribute('aria-expanded', 'false');
      expect(selected).not.toHaveAttribute('aria-current');
      expect(selected).toHaveAttribute('aria-expanded', 'true');
      expect(selected).toHaveAttribute('aria-controls', screen.getByTestId('slot-panel').id);
    });
  });

  describe('shared slot panel', () => {
    it('renders one row: inventory group, divider, then physical group', () => {
      render(<Harness slots={qidiRail} summarySlot={qidiRail[0]} />);
      fireEvent.click(screen.getByTestId('gate-a'));

      const panel = screen.getByRole('region', { name: 'Slot 1A' });
      const row = within(panel).getByRole('group', { name: 'Slot 1A actions' });
      const kids = Array.from(row.querySelectorAll('button, [aria-hidden="true"].w-px'));
      expect(kids.map((el) => el.getAttribute('data-testid') ?? 'divider')).toEqual([
        'slot-action-change', 'slot-action-unassign', 'divider',
        'slot-action-load', 'slot-action-unload', 'slot-action-eject',
      ]);
      for (const button of within(row).getAllByRole('button')) {
        expect(button).toHaveClass('size-11');
        expect(button).not.toHaveAttribute('title');
        expect(button.textContent).toBe('');
      }
      expect(within(panel).getByText('Spool #12')).toBeInTheDocument();
    });

    it('offers Assign for a slot without a spool and omits the divider when there are no physical actions', () => {
      render(
        <Harness
          slots={qidiRail}
          summarySlot={qidiRail[0]}
          panelFor={(s) => ({ title: `Slot ${s.id}`, actions: [{ kind: 'assign', label: `Assign spool to slot ${s.id}`, onClick: noop }] })}
        />,
      );
      fireEvent.click(screen.getByTestId('gate-c'));

      const row = within(screen.getByTestId('slot-panel')).getByRole('group');
      expect(within(row).getAllByRole('button')).toHaveLength(1);
      expect(within(row).getByRole('button', { name: 'Assign spool to slot 1C' })).toBeInTheDocument();
      expect(row.querySelector('.w-px')).toBeNull();
    });

    it('keeps an unsupported or unpermitted action focusable with its reason as the description', () => {
      const onLoad = vi.fn();
      render(
        <Harness
          slots={qidiRail}
          summarySlot={qidiRail[0]}
          panelFor={(s) => ({
            title: `Slot ${s.id}`,
            actions: [
              { kind: 'change', label: 'Change spool', onClick: noop },
              { kind: 'load', label: 'Load filament', onClick: onLoad, disabledReason: 'Printer is offline' },
            ],
          })}
        />,
      );
      fireEvent.click(screen.getByTestId('gate-a'));

      const load = screen.getByTestId('slot-action-load');
      expect(load).toHaveAttribute('aria-disabled', 'true');
      expect(load).not.toBeDisabled();
      expect(load).toHaveAccessibleDescription('Printer is offline');
      fireEvent.click(load);
      expect(onLoad).not.toHaveBeenCalled();

      act(() => load.focus());
      expect(load).toHaveFocus();
      expect(screen.getByRole('tooltip', { hidden: true })).toHaveTextContent('Load filament — Printer is offline');
    });

    it('closes on Escape and returns focus to the slot that opened it', async () => {
      render(<Harness slots={qidiRail} summarySlot={qidiRail[0]} />);
      fireEvent.click(screen.getByTestId('gate-d'));

      fireEvent.keyDown(screen.getByTestId('slot-panel'), { key: 'Escape' });
      expect(screen.queryByTestId('slot-panel')).not.toBeInTheDocument();
      await vi.waitFor(() => expect(screen.getByTestId('gate-d')).toHaveFocus());
    });
  });

  describe('single toolhead', () => {
    it('shows one spool with name and grams, no slot id, no rail and no "Tool"/"T0" text', () => {
      const single = slot({ key: 's', id: '', testId: 'single' });
      render(<Harness slots={[single]} summarySlot={single} mode="single" />);

      const button = screen.getByTestId('single');
      expect(button).toHaveTextContent(/^PLA Black·340 g$/);
      expect(button).toHaveAccessibleName('Filament: PLA Black, 340 g');
      expect(screen.queryByTestId('filament-summary')).not.toBeInTheDocument();
      expect(document.body).not.toHaveTextContent(/\bT0\b|\bTool\b/);

      fireEvent.click(button);
      expect(screen.getByTestId('slot-panel')).not.toHaveTextContent(/\bT0\b/);
    });

    it('renders only a spool outline with "+" when nothing is assigned, without "No spool assigned" copy', () => {
      const empty = slot({
        key: 's', id: '', stock: 'none', spoolId: undefined, displayName: undefined,
        material: undefined, grams: undefined, color: undefined, testId: 'single',
      });
      render(<Harness slots={[empty]} summarySlot={empty} mode="single" />);

      const button = screen.getByTestId('single');
      expect(button).toHaveAccessibleName('Assign spool');
      expect(button).toHaveTextContent('');
      expect(within(button).getByTestId('slot-badge-assign')).toBeInTheDocument();
      expect(document.body).not.toHaveTextContent(/no spool assigned/i);
    });
  });
});

describe('formatting helpers', () => {
  it.each([
    [undefined, undefined],
    [Number.NaN, undefined],
    [-5, undefined],
    [0, '0 g'],
    [0.4, '<1 g'],
    [339.6, '340 g'],
  ])('formatGrams(%s) → %s', (input, expected) => {
    expect(formatGrams(input)).toBe(expected);
  });

  it('does not repeat the material in the display name', () => {
    expect(filamentDisplayName('PLA', 'PLA Matte Black')).toBe('PLA Matte Black');
    expect(filamentDisplayName('PLA', 'Matte Black')).toBe('PLA Matte Black');
    expect(filamentDisplayName('PETG', undefined)).toBe('PETG');
    expect(filamentDisplayName(undefined, undefined)).toBeUndefined();
  });
});
