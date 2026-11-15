/**
 * Simple-mode bed adhesion against a real process-profile API response.
 *
 * The fixture is one verbatim entry from a live
 * `/api/slicer/profiles/process` response for the Micron 180 (0.2 nozzle).
 * Like every Micron 180 0.2 preset, it declares `skirt_loops: "0"` and
 * `raft_layers: "0"` but leaves `brim_type` to its parent, so the UI must not
 * claim "None" and must not pin adhesion keys on submit.
 *
 * The harness mirrors NewSliceJobPage's wiring: converted profile settings are
 * both the editable state and `originalProcessSettings`, the real
 * SlicerSettingsPanel edits them through simpleToOrcaSettings, and
 * buildProcessOverrides produces the submitted overrides.
 */
import { describe, it, expect, afterEach } from 'vitest';
import { render, screen, fireEvent, within, cleanup } from '@testing-library/react';
import '@testing-library/jest-dom';
import { useEffect, useState } from 'react';
import { SlicerSettingsPanel } from '@/features/slicer/components/job/SlicerSettingsPanel';
import type { OrcaProcessProfile } from '@/services/slicerProfilesService';
import { buildProcessOverrides } from '@/features/slicer/utils/orcaOverrideNormalization';
import {
  convertOrcaProcessProfileToSettings,
  orcaToSimpleSettings,
  simpleToOrcaSettings,
} from '../simpleSlicerMappings';
import liveMicronProfile from './__fixtures__/micron180-0.06mm-fine-process-profile.json';

const ADHESION_KEYS = ['brim_type', 'skirt_loops', 'raft_layers'];

function withSettings(patch: Record<string, unknown>, omit: string[] = []): OrcaProcessProfile {
  const settings: Record<string, unknown> = { ...(liveMicronProfile.settings as Record<string, unknown>), ...patch };
  for (const key of omit) delete settings[key];
  return { ...(liveMicronProfile as unknown as OrcaProcessProfile), settings };
}

let latestOverrides: Record<string, unknown> | null = null;

function Harness({ profile }: { profile: OrcaProcessProfile }) {
  const [original] = useState(() => convertOrcaProcessProfileToSettings(profile));
  const [current, setCurrent] = useState(original);
  useEffect(() => {
    latestOverrides = buildProcessOverrides({
      slicerSettings: current,
      originalProcessSettings: original as unknown as Record<string, unknown>,
      advancedProcessSettings: {},
      simpleMode: true,
      engineVersion: undefined,
    });
  }, [current, original]);
  return (
    <SlicerSettingsPanel
      settings={orcaToSimpleSettings(current)}
      onSettingsChange={(next) => setCurrent((prev) => simpleToOrcaSettings(next, prev))}
      simpleMode
    />
  );
}

function renderProfile(profile: OrcaProcessProfile) {
  render(<Harness profile={profile} />);
  const select = screen.getByRole('combobox', { name: /bed adhesion/i }) as HTMLSelectElement;
  const overrides = () => {
    if (!latestOverrides) throw new Error('Harness has not computed overrides');
    return latestOverrides;
  };
  const adhesion = () => Object.fromEntries(Object.entries(overrides()).filter(([k]) => ADHESION_KEYS.includes(k)));
  return { select, overrides, adhesion };
}

function selectedLabel(select: HTMLSelectElement) {
  return select.options[select.selectedIndex]?.textContent;
}

afterEach(() => {
  cleanup();
  latestOverrides = null;
});

describe('Simple bed adhesion with the live Micron 180 process profile', () => {
  it('uses the response verbatim: brim_type inherited, skirt and raft explicitly "0"', () => {
    expect(liveMicronProfile.name).toBe('0.06mm Fine 0.2 nozzle @Micron 180');
    expect(liveMicronProfile.settings).not.toHaveProperty('brim_type');
    expect(liveMicronProfile.settings).toMatchObject({ skirt_loops: '0', raft_layers: '0' });
  });

  it('labels the inherited brim as unknown and submits no adhesion keys', () => {
    const { select, adhesion } = renderProfile(liveMicronProfile as unknown as OrcaProcessProfile);

    expect(select.value).toBe('profile');
    expect(selectedLabel(select)).toBe('Profile-controlled (inherited value unknown)');
    expect(within(select).getByRole('option', { name: 'Profile-controlled (inherited value unknown)' })).toBeDisabled();
    expect(within(select).queryByRole('option', { name: 'None' })).toBeInTheDocument();
    expect(selectedLabel(select)).not.toBe('None');
    expect(adhesion()).toEqual({});
  });

  it('keeps adhesion unpinned through an unrelated Simple edit', () => {
    const { adhesion, overrides } = renderProfile(liveMicronProfile as unknown as OrcaProcessProfile);

    const perimeters = screen.getByRole('spinbutton', { name: /perimeters/i });
    fireEvent.change(perimeters, { target: { value: '5' } });
    fireEvent.blur(perimeters);

    expect(overrides()).toHaveProperty('wall_loops', '5');
    expect(adhesion()).toEqual({});
  });

  it.each([
    ['brim', { brim_type: 'outer_only', skirt_loops: '0', raft_layers: '0' }],
    ['none', { brim_type: 'no_brim', skirt_loops: '0', raft_layers: '0' }],
    ['skirt', { brim_type: 'no_brim', skirt_loops: '1', raft_layers: '0' }],
    ['raft', { brim_type: 'no_brim', skirt_loops: '0', raft_layers: '3' }],
  ] as const)('an explicit %s replaces the inherited adhesion with all three keys', (choice, expected) => {
    const { select, adhesion } = renderProfile(liveMicronProfile as unknown as OrcaProcessProfile);

    fireEvent.change(select, { target: { value: choice } });

    expect(select.value).toBe(choice);
    expect(adhesion()).toEqual(expected);
  });

  it('labels a compound skirt + brim and submits no adhesion keys', () => {
    const { select, adhesion } = renderProfile(withSettings({ brim_type: 'outer_only', skirt_loops: '2' }));

    expect(select.value).toBe('profile');
    expect(selectedLabel(select)).toBe('Profile: skirt + brim');
    expect(adhesion()).toEqual({});
  });

  it('shows explicit zero adhesion as None and pins it so Orca cannot fall back to auto_brim', () => {
    const { select, adhesion } = renderProfile(withSettings({ brim_type: 'no_brim' }));

    expect(select.value).toBe('none');
    expect(selectedLabel(select)).toBe('None');
    expect(adhesion()).toEqual({ brim_type: 'no_brim', skirt_loops: '0', raft_layers: '0' });
  });

  it('treats an inherited zero skirt (key absent) as unknown, not as no skirt', () => {
    const { select, adhesion } = renderProfile(withSettings({ brim_type: 'no_brim' }, ['skirt_loops']));

    expect(select.value).toBe('profile');
    expect(selectedLabel(select)).toBe('Profile-controlled (inherited value unknown)');
    expect(adhesion()).toEqual({});
  });
});
