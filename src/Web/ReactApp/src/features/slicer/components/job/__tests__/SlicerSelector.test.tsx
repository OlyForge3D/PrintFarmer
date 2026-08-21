import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, within, act } from '@testing-library/react';
import '@testing-library/jest-dom';
import { SlicerSelector, type SlicerVersionChoice } from '../SlicerSelector';

vi.mock('@/common/utils/slicerEngineIcon', () => ({
  getSlicerIconSrc: () => '/icons/orca.svg',
}));

const engineOptions = [
  { label: 'OrcaSlicer 2.4.2', value: 1 },
  { label: 'PrusaSlicer 2.9.0', value: 2 },
];

function renderSelector(overrides: Partial<React.ComponentProps<typeof SlicerSelector>> = {}) {
  const onSlicerChange = vi.fn();
  const onVersionChange = vi.fn();
  const utils = render(
    <SlicerSelector
      selectedSlicerId={1}
      onSlicerChange={onSlicerChange}
      engineOptions={engineOptions}
      engineName="OrcaSlicer"
      {...overrides}
    />,
  );
  return { ...utils, onSlicerChange, onVersionChange };
}

const TWO_ONLINE: SlicerVersionChoice[] = [
  { version: '2.4.2', available: true },
  { version: '2.3.1', available: true },
];

/**
 * The reported case (issue #1772): 2.3.1 ships in the plugin registry but has
 * no online worker — either never configured, or its container is gone and
 * only a stale registration row remains.
 */
const ONE_ONLINE_ONE_DEAD: SlicerVersionChoice[] = [
  { version: '2.4.2', available: true },
  { version: '2.3.1', available: false },
];

describe('SlicerSelector', () => {
  describe('engine cards', () => {
    it('renders each engine with its name and version split apart', () => {
      renderSelector();

      expect(screen.getByText('OrcaSlicer')).toBeInTheDocument();
      expect(screen.getByText('PrusaSlicer')).toBeInTheDocument();
      // Bare (no leading "v") versions must still parse out of the label.
      expect(screen.getByText('2.9.0')).toBeInTheDocument();
    });

    it('marks the selected engine as pressed and notifies on change', () => {
      const { onSlicerChange } = renderSelector();

      const prusa = screen.getByRole('button', { name: /PrusaSlicer/ });
      expect(screen.getByRole('button', { name: /OrcaSlicer/ })).toHaveAttribute('aria-pressed', 'true');
      expect(prusa).toHaveAttribute('aria-pressed', 'false');

      fireEvent.click(prusa);
      expect(onSlicerChange).toHaveBeenCalledWith(2);
    });

    it('shows the effective version on the selected card so card and picker agree', () => {
      renderSelector({
        versionEntries: TWO_ONLINE,
        latestVersion: '2.4.2',
        selectedVersion: '2.3.1',
      });

      const orcaCard = screen.getByRole('button', { name: /OrcaSlicer/ });
      expect(within(orcaCard).getByText('2.3.1')).toBeInTheDocument();
    });
  });

  describe('unavailable versions (issue #1772)', () => {
    it('never offers a version that has no online worker', () => {
      renderSelector({ versionEntries: ONE_ONLINE_ONE_DEAD, latestVersion: '2.4.2' });

      // Unpickable, so it must not appear at all — not as a disabled option,
      // and not as an "(offline)" label.
      expect(screen.queryByText(/2\.3\.1/)).not.toBeInTheDocument();
      expect(screen.queryByText(/offline/i)).not.toBeInTheDocument();
    });

    it('collapses the picker when only one version is selectable', () => {
      renderSelector({ versionEntries: ONE_ONLINE_ONE_DEAD, latestVersion: '2.4.2' });

      // One option is not a choice; the engine card already shows the version.
      expect(screen.queryByText('Engine version')).not.toBeInTheDocument();
    });

    it('warns instead of offering dead options when no version is available', () => {
      renderSelector({
        versionEntries: [
          { version: '2.4.2', available: false },
          { version: '2.3.1', available: false },
        ],
      });

      expect(screen.queryByRole('group', { name: 'Engine version' })).not.toBeInTheDocument();
      expect(screen.getByText(/No online OrcaSlicer worker is registered/i)).toBeInTheDocument();
    });

    it('keeps a pinned version visible once it goes offline so the pin is diagnosable', () => {
      renderSelector({
        versionEntries: ONE_ONLINE_ONE_DEAD,
        latestVersion: '2.4.2',
        selectedVersion: '2.3.1',
      });

      const group = screen.getByRole('group', { name: 'Engine version' });
      expect(within(group).getByRole('button', { name: /2\.3\.1/ })).toHaveAttribute('aria-pressed', 'true');
      expect(screen.getByText(/2\.3\.1 has no online worker/i)).toBeInTheDocument();
    });
  });

  describe('version pills', () => {
    it('renders Latest plus one pill per selectable version, defaulting to Latest', () => {
      renderSelector({ versionEntries: TWO_ONLINE, latestVersion: '2.4.2' });

      const group = screen.getByRole('group', { name: 'Engine version' });
      expect(within(group).getByRole('button', { name: /Latest/ })).toHaveAttribute('aria-pressed', 'true');
      expect(within(group).getByRole('button', { name: '2.4.2' })).toBeInTheDocument();
      expect(within(group).getByRole('button', { name: '2.3.1' })).toBeInTheDocument();
    });

    it('pins a version when its pill is clicked and unpins via Latest', () => {
      const onVersionChange = vi.fn();
      renderSelector({ versionEntries: TWO_ONLINE, latestVersion: '2.4.2', onVersionChange });

      const group = screen.getByRole('group', { name: 'Engine version' });
      fireEvent.click(within(group).getByRole('button', { name: '2.3.1' }));
      expect(onVersionChange).toHaveBeenCalledWith('2.3.1');

      fireEvent.click(within(group).getByRole('button', { name: /Latest/ }));
      expect(onVersionChange).toHaveBeenCalledWith(undefined);
    });

    it('falls back to a dropdown when there are more than three selectable versions', () => {
      renderSelector({
        versionEntries: [
          { version: '2.4.2', available: true },
          { version: '2.3.1', available: true },
          { version: '2.2.0', available: true },
          { version: '2.1.0', available: true },
        ],
        latestVersion: '2.4.2',
      });

      expect(screen.queryByRole('group', { name: 'Engine version' })).not.toBeInTheDocument();
      const select = screen.getByRole('combobox', { name: 'Engine version' });
      expect(within(select).getAllByRole('option')).toHaveLength(5); // Latest + 4
    });
  });

  describe('layout and guidance', () => {
    it('renders the version picker inside the Slicer Engine panel, not a sibling box', () => {
      const { container } = renderSelector({ versionEntries: TWO_ONLINE, latestVersion: '2.4.2' });

      // The component emits exactly one panel; engine cards and the version
      // picker must both live inside it.
      const panel = container.firstElementChild as HTMLElement;
      expect(panel).toHaveClass('bg-pf-panel');
      expect(within(panel).getByText('Slicer Engine')).toBeInTheDocument();
      expect(within(panel).getByRole('group', { name: 'Engine version' })).toBeInTheDocument();
    });

    it('exposes guidance through an aria-describedby-wired tooltip rather than inline prose', () => {
      renderSelector({ versionEntries: TWO_ONLINE, latestVersion: '2.4.2' });

      const infoButton = screen.getByRole('button', { name: 'More information about engine version' });
      const describedBy = infoButton.getAttribute('aria-describedby');
      expect(describedBy).toBeTruthy();

      const tooltip = document.getElementById(describedBy!);
      expect(tooltip).toHaveTextContent(/Pins the slice job to a specific OrcaSlicer engine/i);
      expect(tooltip).toHaveTextContent(/Only versions with an online worker are listed/i);
      expect(tooltip?.className).toContain('hidden');

      act(() => {
        infoButton.focus();
      });
      expect(tooltip?.className).not.toContain('hidden');
    });
  });
});
