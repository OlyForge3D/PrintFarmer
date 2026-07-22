import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import '@testing-library/jest-dom';
import { PrinterProfileSelector } from '../PrinterProfileSelector';

const defaultProps = {
  availableManufacturers: ['Prusa'],
  availablePrinterModels: [{ key: 'prusa-mk4', name: 'Original Prusa MK4', modelId: 'mk4' }],
  availableMachineProfiles: [
    {
      id: 'machine-profile-1',
      name: 'Original Prusa MK4 0.4 nozzle',
      slicerType: 'OrcaSlicer',
      isDefault: false,
      isSystem: true,
      isPublic: true,
      hash: 'hash-1',
      profileType: 'machine' as const,
      manufacturer: 'Prusa',
      nozzleDiameter: 0.4,
    },
  ],
  selectedManufacturer: 'Prusa',
  selectedPrinterModel: 'prusa-mk4',
  selectedMachineProfileId: 'machine-profile-1',
  onManufacturerChange: vi.fn(),
  onPrinterModelChange: vi.fn(),
  onMachineProfileChange: vi.fn(),
};

describe('PrinterProfileSelector', () => {
  it('associates each visible field label with its select', () => {
    render(<PrinterProfileSelector {...defaultProps} />);

    expect(screen.getByRole('combobox', { name: 'Manufacturer' })).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Printer Model' })).toBeInTheDocument();
    expect(screen.getByRole('combobox', { name: 'Machine Profile' })).toBeInTheDocument();
  });
});
