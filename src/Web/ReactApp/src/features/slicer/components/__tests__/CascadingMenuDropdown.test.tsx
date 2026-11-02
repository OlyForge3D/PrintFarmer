import { afterEach, describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import { FilamentProfileDropdown } from '../CascadingMenuDropdown';
import type { OrcaFilamentProfile } from '@/services/slicerProfilesService';

function filament(name: string, material: string): OrcaFilamentProfile {
  return {
    name,
    material,
    nozzleTemperature: 210,
    bedTemperature: 60,
    printSpeed: 50,
    compatible_printers: [],
  };
}

const noopFilter = { hiddenManufacturers: [], hiddenMaterials: [] };

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('FilamentProfileDropdown trigger', () => {
  it('shows the filament material type and a full-name tooltip for the selection', () => {
    const longName = 'AliZ Premium PLA Galaxy Black @RatRig V-Core 4 0.4 nozzle';
    render(
      <FilamentProfileDropdown
        profiles={[filament(longName, 'PLA')]}
        customProfiles={[]}
        selectedProfileName={longName}
        onSelect={vi.fn()}
        filterConfig={noopFilter}
        onFilterConfigChange={vi.fn()}
      />,
    );

    // Material type badge is visible.
    expect(screen.getByText('PLA')).toBeInTheDocument();
    // The trigger carries the FULL profile name as a hover tooltip even though
    // the visible label truncates.
    const trigger = screen.getByRole('button');
    expect(trigger).toHaveAttribute('title', longName);
    expect(trigger).toHaveTextContent('AliZ Premium PLA');
  });

  it('renders the placeholder with no material badge when nothing is selected', () => {
    render(
      <FilamentProfileDropdown
        profiles={[filament('Generic PLA', 'PLA')]}
        customProfiles={[]}
        selectedProfileName=""
        onSelect={vi.fn()}
        filterConfig={noopFilter}
        onFilterConfigChange={vi.fn()}
      />,
    );
    const trigger = screen.getByRole('button');
    expect(trigger).toHaveTextContent('-- Select Filament --');
    // No material badge / no tooltip when there is no selection.
    expect(trigger).not.toHaveAttribute('title');
  });

  it('lists individual profiles by NAME (not just the material type) when expanded', () => {
    const onSelect = vi.fn();
    render(
      <FilamentProfileDropdown
        profiles={[
          filament('Generic ABS @0.4 nozzle', 'ABS'),
          filament('Generic ABS High Speed', 'ABS'),
          filament('Generic PLA @0.4 nozzle', 'PLA'),
        ].map(p => ({ ...p, manufacturer: 'Generic' }))}
        customProfiles={[]}
        selectedProfileName=""
        onSelect={onSelect}
        filterConfig={noopFilter}
        onFilterConfigChange={vi.fn()}
      />,
    );

    // Open the dropdown, then expand the Generic manufacturer group.
    fireEvent.click(screen.getByRole('button'));
    fireEvent.click(screen.getByText('Generic'));

    // Each profile is shown by its full name — including BOTH ABS profiles that
    // previously collapsed to a single "ABS" material row.
    expect(screen.getByText('Generic ABS @0.4 nozzle')).toBeInTheDocument();
    expect(screen.getByText('Generic ABS High Speed')).toBeInTheDocument();
    expect(screen.getByText('Generic PLA @0.4 nozzle')).toBeInTheDocument();

    // Material sub-headers group the profiles by polymer (ABS / PLA).
    expect(screen.getByText('ABS')).toBeInTheDocument();
    expect(screen.getByText('PLA')).toBeInTheDocument();

    fireEvent.click(screen.getByText('Generic ABS High Speed'));
    expect(onSelect).toHaveBeenCalledWith('Generic ABS High Speed', 'system');

    // Nozzle/bed temperatures are NOT shown in the menu rows.
    expect(screen.queryByText(/210°\/60°/)).not.toBeInTheDocument();
    expect(screen.queryByText(/°\//)).not.toBeInTheDocument();
  });

  it('flips above a low trigger, clamps width, and repositions on resize and scrolling', () => {
    vi.stubGlobal('innerWidth', 300);
    vi.stubGlobal('innerHeight', 500);
    const onSelect = vi.fn();
    render(
      <FilamentProfileDropdown
        profiles={[]}
        customProfiles={[{ id: 'custom', name: 'Saved filament' }]}
        selectedProfileName=""
        onSelect={onSelect}
        filterConfig={noopFilter}
        onFilterConfigChange={vi.fn()}
      />,
    );
    const trigger = screen.getByRole('button');
    let rect = { top: 460, bottom: 490, left: 250, width: 100 } as DOMRect;
    vi.spyOn(trigger, 'getBoundingClientRect').mockImplementation(() => rect);
    fireEvent.click(trigger);
    const menu = screen.getByRole('dialog', { name: 'Filament selector' });
    expect(menu).toHaveStyle({ top: 'auto', bottom: '42px', left: '8px', width: '284px', maxHeight: '400px' });
    vi.stubGlobal('innerWidth', 1000);
    vi.stubGlobal('innerHeight', 900);
    fireEvent(window, new Event('resize'));
    expect(menu).toHaveStyle({ top: '492px', bottom: 'auto', left: '250px', width: '320px', maxHeight: '400px' });
    rect = { top: 600, bottom: 630, left: 950, width: 100 } as DOMRect;
    fireEvent(window, new Event('scroll'));
    expect(menu).toHaveStyle({ top: 'auto', bottom: '302px', left: '672px', maxHeight: '400px' });
    fireEvent.click(screen.getByRole('button', { name: /Saved filament/ }));
    expect(onSelect).toHaveBeenCalledWith('Saved filament', 'custom');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });
});
