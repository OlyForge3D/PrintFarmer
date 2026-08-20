import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { MachineProfileSelectorModal, type MachineProfileChoice } from '../MachineProfileSelectorModal';

/**
 * Profile names and diameters below are verbatim from
 * GET /api/slicer/profiles/machine/for-model for Prusa CORE One.
 *
 * The pairs matter: standard and HF share nozzleDiameter AND printerVariant,
 * and nozzleType is empty on both, so the name is the only differentiator.
 */
const CORE_ONE: MachineProfileChoice[] = [
  { name: 'Prusa CORE One 0.25 nozzle', nozzleDiameter: 0.25, isSystem: true },
  { name: 'Prusa CORE One 0.4 nozzle', nozzleDiameter: 0.4, isSystem: true },
  { name: 'Prusa CORE One HF 0.4 nozzle', nozzleDiameter: 0.4, isSystem: true },
  { name: 'Prusa CORE One 0.6 nozzle', nozzleDiameter: 0.6, isSystem: true },
  { name: 'Prusa CORE One HF 0.6 nozzle', nozzleDiameter: 0.6, isSystem: true },
];

function renderModal(overrides: Partial<React.ComponentProps<typeof MachineProfileSelectorModal>> = {}) {
  const onSelect = vi.fn();
  const onClose = vi.fn();
  render(
    <MachineProfileSelectorModal
      isOpen
      profiles={CORE_ONE}
      selectedProfileName="Prusa CORE One 0.4 nozzle"
      onSelect={onSelect}
      onClose={onClose}
      {...overrides}
    />,
  );
  return { onSelect, onClose };
}

/** Rows in one nozzle section. */
function rowsIn(nozzleLabel: string) {
  const section = screen.getByRole('region', { name: nozzleLabel })
    ?? screen.getByLabelText(nozzleLabel);
  return within(section).getAllByRole('button');
}

describe('MachineProfileSelectorModal', () => {
  beforeEach(() => vi.clearAllMocks());

  it('shows both profiles that share a nozzle diameter', () => {
    renderModal();
    expect(rowsIn('0.4 mm machine profiles')).toHaveLength(2);
  });

  it('actually trims the nozzle token — not the raw-name fallback', () => {
    // Regression guard: building labels across the whole profile set collides
    // for any multi-nozzle printer and silently falls back to raw names, which
    // disabled trimming entirely. Assert the visible label node exactly.
    renderModal();
    const names = rowsIn('0.4 mm machine profiles').map(
      (r) => r.querySelector('span.truncate')?.textContent?.trim(),
    );
    expect(names).toEqual(['Prusa CORE One', 'Prusa CORE One HF']);
    // No row may still carry the redundant nozzle token in its label.
    names.forEach((n) => expect(n).not.toMatch(/nozzle/i));
  });

  it('badges the high-flow variant without asserting unverified hardware capability', () => {
    renderModal();
    const hfRow = rowsIn('0.4 mm machine profiles').find((r) => /HF/.test(r.textContent ?? ''))!;
    expect(within(hfRow).getByText('HF')).toBeInTheDocument();
    expect(within(hfRow).getByText('Name indicates a high-flow variant')).toBeInTheDocument();
  });

  it('reports the full profile name, never the trimmed label', () => {
    const { onSelect } = renderModal();
    const hfRow = rowsIn('0.4 mm machine profiles').find((r) => /HF/.test(r.textContent ?? ''))!;
    fireEvent.click(hfRow);
    expect(onSelect).toHaveBeenCalledWith('Prusa CORE One HF 0.4 nozzle');
  });

  it('marks exactly the selected row via aria-pressed', () => {
    renderModal();
    const rows = rowsIn('0.4 mm machine profiles');
    const standard = rows.find((r) => !/HF/.test(r.textContent ?? ''))!;
    const highFlow = rows.find((r) => /HF/.test(r.textContent ?? ''))!;
    expect(standard).toHaveAttribute('aria-pressed', 'true');
    expect(highFlow).toHaveAttribute('aria-pressed', 'false');
  });

  it('moves the pressed state to the HF row when it is the selection', () => {
    renderModal({ selectedProfileName: 'Prusa CORE One HF 0.4 nozzle' });
    const rows = rowsIn('0.4 mm machine profiles');
    const standard = rows.find((r) => !/HF/.test(r.textContent ?? ''))!;
    const highFlow = rows.find((r) => /HF/.test(r.textContent ?? ''))!;
    expect(standard).toHaveAttribute('aria-pressed', 'false');
    expect(highFlow).toHaveAttribute('aria-pressed', 'true');
  });

  it('filters by nozzle without losing same-nozzle variants', () => {
    renderModal();
    const facet = screen.getByRole('group', { name: 'Filter by nozzle diameter' });
    fireEvent.click(within(facet).getByRole('button', { name: /0\.6 mm/ }));

    expect(screen.queryByLabelText('0.25 mm machine profiles')).not.toBeInTheDocument();
    expect(rowsIn('0.6 mm machine profiles')).toHaveLength(2);
  });

  it('searches across the full profile name including the nozzle token', () => {
    renderModal();
    fireEvent.change(screen.getByLabelText('Search machine profiles'), { target: { value: 'HF 0.6' } });
    expect(rowsIn('0.6 mm machine profiles')).toHaveLength(1);
    expect(screen.queryByLabelText('0.4 mm machine profiles')).not.toBeInTheDocument();
  });

  it('offers a way out of an over-filtered empty state', () => {
    renderModal();
    fireEvent.change(screen.getByLabelText('Search machine profiles'), { target: { value: 'zzz' } });
    expect(screen.getByText(/No machine profiles match the current search/)).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }));
    expect(rowsIn('0.4 mm machine profiles')).toHaveLength(2);
  });

  it('distinguishes an empty printer from an over-filtered list', () => {
    renderModal({ profiles: [], selectedProfileName: '' });
    expect(screen.getByText('No machine profiles available for this printer.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Clear filters' })).not.toBeInTheDocument();
  });

  it('groups user profiles separately from system presets', () => {
    renderModal({
      profiles: [
        ...CORE_ONE,
        { name: 'My CORE One tune 0.4 nozzle', nozzleDiameter: 0.4, isSystem: false },
      ],
    });
    const mine = rowsIn('My machine profiles');
    expect(mine).toHaveLength(1);
    expect(mine[0]).toHaveTextContent('My CORE One tune');
    // The user profile must not also appear in the system group for its nozzle.
    const system = rowsIn('0.4 mm machine profiles');
    expect(system.some((r) => /My CORE One tune/.test(r.textContent ?? ''))).toBe(false);
  });

  it('hides the nozzle facet when there is only one nozzle', () => {
    renderModal({
      profiles: [{ name: 'Phrozen Arco 0.4 nozzle', nozzleDiameter: 0.4, isSystem: true }],
      selectedProfileName: 'Phrozen Arco 0.4 nozzle',
    });
    expect(screen.queryByRole('group', { name: 'Filter by nozzle diameter' })).not.toBeInTheDocument();
  });

  it('buckets profiles with an unresolved nozzle under Other', () => {
    renderModal({
      profiles: [
        { name: 'Prusa CORE One 0.4 nozzle', nozzleDiameter: 0.4, isSystem: true },
        { name: 'Mystery machine', isSystem: true },
      ],
    });
    const other = rowsIn('Machine profiles with unknown nozzle');
    expect(other).toHaveLength(1);
    expect(other[0]).toHaveTextContent('Mystery machine');
  });
});
