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

describe('MachineProfileSelectorModal', () => {
  beforeEach(() => vi.clearAllMocks());

  it('shows both profiles that share a nozzle diameter', () => {
    renderModal();
    const group = screen.getByRole('radiogroup', { name: '0.4 mm machine profiles' });
    expect(within(group).getAllByRole('radio')).toHaveLength(2);
  });

  it('badges the high-flow variant and explains it', () => {
    renderModal();
    const group = screen.getByRole('radiogroup', { name: '0.4 mm machine profiles' });
    const hfRow = within(group).getByRole('radio', { name: /Prusa CORE One HF/ });
    expect(within(hfRow).getByText('HF')).toBeInTheDocument();
    expect(within(hfRow).getByText(/High-flow hotend/)).toBeInTheDocument();
  });

  it('keeps HF and standard labels distinct after trimming the nozzle token', () => {
    renderModal();
    const group = screen.getByRole('radiogroup', { name: '0.4 mm machine profiles' });
    const labels = within(group).getAllByRole('radio').map((r) => r.textContent ?? '');
    expect(labels.some((l) => l.includes('Prusa CORE One HF'))).toBe(true);
    expect(labels.some((l) => /Prusa CORE One(?! HF)/.test(l))).toBe(true);
  });

  it('reports the full profile name, never the trimmed label', () => {
    const { onSelect } = renderModal();
    const group = screen.getByRole('radiogroup', { name: '0.4 mm machine profiles' });
    fireEvent.click(within(group).getByRole('radio', { name: /Prusa CORE One HF/ }));
    expect(onSelect).toHaveBeenCalledWith('Prusa CORE One HF 0.4 nozzle');
  });

  it('marks the current selection', () => {
    renderModal();
    const group = screen.getByRole('radiogroup', { name: '0.4 mm machine profiles' });
    const selected = within(group).getAllByRole('radio').filter((r) => r.getAttribute('aria-checked') === 'true');
    expect(selected).toHaveLength(1);
    expect(selected[0]).toHaveTextContent('Prusa CORE One');
  });

  it('filters by nozzle without losing same-nozzle variants', () => {
    renderModal();
    const facet = screen.getByRole('radiogroup', { name: 'Filter by nozzle diameter' });
    fireEvent.click(within(facet).getByRole('radio', { name: '0.6 mm' }));

    expect(screen.queryByRole('radiogroup', { name: '0.25 mm machine profiles' })).not.toBeInTheDocument();
    const group = screen.getByRole('radiogroup', { name: '0.6 mm machine profiles' });
    expect(within(group).getAllByRole('radio')).toHaveLength(2);
  });

  it('searches across the full profile name including the nozzle token', () => {
    renderModal();
    fireEvent.change(screen.getByLabelText('Search machine profiles'), { target: { value: 'HF 0.6' } });
    const group = screen.getByRole('radiogroup', { name: '0.6 mm machine profiles' });
    expect(within(group).getAllByRole('radio')).toHaveLength(1);
    expect(screen.queryByRole('radiogroup', { name: '0.4 mm machine profiles' })).not.toBeInTheDocument();
  });

  it('shows an empty state when nothing matches', () => {
    renderModal();
    fireEvent.change(screen.getByLabelText('Search machine profiles'), { target: { value: 'zzz' } });
    expect(screen.getByText('No machine profiles match.')).toBeInTheDocument();
  });

  it('groups user profiles separately from system presets', () => {
    renderModal({
      profiles: [
        ...CORE_ONE,
        { name: 'My CORE One tune 0.4 nozzle', nozzleDiameter: 0.4, isSystem: false },
      ],
    });
    const mine = screen.getByRole('radiogroup', { name: 'My machine profiles' });
    expect(within(mine).getAllByRole('radio')).toHaveLength(1);
    expect(within(mine).getByRole('radio', { name: /My CORE One tune/ })).toBeInTheDocument();
    // The user profile must not also appear in the system group for its nozzle.
    const system = screen.getByRole('radiogroup', { name: '0.4 mm machine profiles' });
    expect(within(system).queryByRole('radio', { name: /My CORE One tune/ })).not.toBeInTheDocument();
  });

  it('hides the nozzle facet when there is only one nozzle', () => {
    renderModal({
      profiles: [{ name: 'Phrozen Arco 0.4 nozzle', nozzleDiameter: 0.4, isSystem: true }],
      selectedProfileName: 'Phrozen Arco 0.4 nozzle',
    });
    expect(screen.queryByRole('radiogroup', { name: 'Filter by nozzle diameter' })).not.toBeInTheDocument();
  });

  it('buckets profiles with an unresolved nozzle under Other', () => {
    renderModal({
      profiles: [
        { name: 'Prusa CORE One 0.4 nozzle', nozzleDiameter: 0.4, isSystem: true },
        { name: 'Mystery machine', isSystem: true },
      ],
    });
    const other = screen.getByRole('radiogroup', { name: 'Machine profiles with unknown nozzle' });
    expect(within(other).getByRole('radio', { name: /Mystery machine/ })).toBeInTheDocument();
  });
});
