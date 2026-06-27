import { describe, it, expect, vi } from 'vitest';
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
    compatiblePrinters: [],
  };
}

const noopFilter = { hiddenManufacturers: [], hiddenMaterials: [] };

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

    fireEvent.click(screen.getByText('Generic ABS High Speed'));
    expect(onSelect).toHaveBeenCalledWith('Generic ABS High Speed', 'system');
  });
});
