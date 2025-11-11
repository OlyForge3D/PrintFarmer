import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { userEvent } from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { OrcaImportWizard, type OrcaBundlePreview } from '@farm/slicers-orcaslicer-v2_3_x';

// Mock the orcaProfilesService
vi.mock('@farm/slicers-orcaslicer-v2_3_x/services/orcaProfilesService');

import { orcaProfilesService } from '@farm/slicers-orcaslicer-v2_3_x/services/orcaProfilesService';

describe('OrcaImportWizard E2E Tests', () => {
  let queryClient: QueryClient;
  let user: ReturnType<typeof userEvent.setup>;

  const mockPreview: OrcaBundlePreview = {
    printers: [
      {
        name: 'Bambu Lab X1 Carbon',
        manufacturer: 'Bambu Lab',
        bedWidth: 256,
        bedDepth: 256,
        maxZHeight: 256,
        nozzleDiameter: 0.4,
          maxBedTemperature: 110,
          maxHotendTemperature: 300,
          hasHeatedBed: true,
          rawParameters: {},
      },
      {
        name: 'Prusa MK4',
        manufacturer: 'Prusa Research',
        bedWidth: 250,
        bedDepth: 210,
        maxZHeight: 220,
        nozzleDiameter: 0.4,
          maxBedTemperature: 120,
          maxHotendTemperature: 300,
          hasHeatedBed: true,
          rawParameters: {},
      },
    ],
    filaments: [
      {
        name: 'Generic PLA',
        filamentType: 'PLA',
        nozzleTemperature: 215,
        bedTemperature: 60,
          rawParameters: {},
      },
      {
        name: 'Generic PETG',
        filamentType: 'PETG',
        nozzleTemperature: 240,
        bedTemperature: 80,
          rawParameters: {},
      },
    ],
    processes: [
      {
        name: '0.20mm SPEED @BBL X1C',
        layerHeight: 0.2,
          firstLayerHeight: 0.2,
        infillPercentage: 15,
          enableSupports: false,
          perimeters: 2,
          topLayers: 5,
          bottomLayers: 4,
        quality: 'Standard',
          rawParameters: {},
      },
      {
        name: '0.12mm FINE @BBL X1C',
        layerHeight: 0.12,
          firstLayerHeight: 0.2,
        infillPercentage: 20,
          enableSupports: false,
          perimeters: 3,
          topLayers: 7,
          bottomLayers: 6,
        quality: 'Fine',
          rawParameters: {},
      },
    ],
    metadata: {
      source: 'OrcaSlicer',
      version: '2.0.0',
      exportedAt: '2025-01-15T12:00:00Z',
    },
  };

  const mockBundleJson = JSON.stringify({
    machine: { 'Bambu Lab X1 Carbon': {}, 'Prusa MK4': {} },
    filament: { 'Generic PLA': {}, 'Generic PETG': {} },
    process: { '0.20mm SPEED @BBL X1C': {}, '0.12mm FINE @BBL X1C': {} },
  });

  beforeEach(() => {
    queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
        mutations: { retry: false },
      },
    });
    user = userEvent.setup();
    vi.clearAllMocks();
  });

  const renderWizard = () => {
    return render(
      <QueryClientProvider client={queryClient}>
        <OrcaImportWizard />
      </QueryClientProvider>
    );
  };

  describe('Upload Step', () => {
    it('renders upload step initially', () => {
      renderWizard();

      expect(screen.getByText('Upload OrcaSlicer Bundle')).toBeInTheDocument();
      expect(
        screen.getByText(/Select a config bundle JSON file exported from OrcaSlicer/)
      ).toBeInTheDocument();
        expect(document.querySelector('#bundle-upload')).toBeTruthy();
    });

    it('shows file loaded state after file selection', async () => {
      renderWizard();

      const file = new File([mockBundleJson], 'bundle.json', {
        type: 'application/json',
      });
      const input = document.querySelector("#bundle-upload")! as HTMLInputElement;

      await user.upload(input, file);

      await waitFor(() => {
        expect(screen.getByText('File loaded')).toBeInTheDocument();
      });
    });

    it('shows preview button after file is loaded', async () => {
      renderWizard();

      const file = new File([mockBundleJson], 'bundle.json', {
        type: 'application/json',
      });
      const input = document.querySelector("#bundle-upload")! as HTMLInputElement;

      await user.upload(input, file);

      await waitFor(() => {
        expect(screen.getByRole('button', { name: /Preview Bundle/i })).toBeInTheDocument();
      });
    });

    it('handles preview button click and transitions to preview step', async () => {
      vi.mocked(orcaProfilesService.previewBundle).mockResolvedValue(mockPreview);

      renderWizard();

      const file = new File([mockBundleJson], 'bundle.json', {
        type: 'application/json',
      });
      const input = document.querySelector("#bundle-upload")! as HTMLInputElement;

      await user.upload(input, file);

      const previewButton = await screen.findByRole('button', { name: /Preview Bundle/i });
      await user.click(previewButton);

      await waitFor(() => {
        expect(orcaProfilesService.previewBundle).toHaveBeenCalledWith(mockBundleJson);
        expect(screen.getByText('Bundle Preview')).toBeInTheDocument();
      });
    });

    it('displays error message on preview failure', async () => {
      vi.mocked(orcaProfilesService.previewBundle).mockRejectedValue(
        new Error('Invalid bundle format')
      );

      renderWizard();

      const file = new File([mockBundleJson], 'bundle.json', {
        type: 'application/json',
      });
      const input = document.querySelector("#bundle-upload")! as HTMLInputElement;

      await user.upload(input, file);

      const previewButton = await screen.findByRole('button', { name: /Preview Bundle/i });
      await user.click(previewButton);

      await waitFor(() => {
        expect(screen.getByText('Failed to parse bundle')).toBeInTheDocument();
        expect(screen.getByText('Invalid bundle format')).toBeInTheDocument();
      });
    });
  });

  describe('Preview Step', () => {
    beforeEach(async () => {
      vi.mocked(orcaProfilesService.previewBundle).mockResolvedValue(mockPreview);
    });

    const uploadAndPreview = async () => {
      renderWizard();

      const file = new File([mockBundleJson], 'bundle.json', {
        type: 'application/json',
      });
      const input = document.querySelector("#bundle-upload")! as HTMLInputElement;

      await user.upload(input, file);
      const previewButton = await screen.findByRole('button', { name: /Preview Bundle/i });
      await user.click(previewButton);

      await waitFor(() => {
        expect(screen.getByText('Bundle Preview')).toBeInTheDocument();
      });
    };

    it('displays preset counts correctly', async () => {
      await uploadAndPreview();

        // Check for the count cards with numbers
        const counts = screen.getAllByText('2');
        expect(counts.length).toBeGreaterThanOrEqual(3); // 2 printers, 2 filaments, 2 processes
      expect(screen.getByText('printer presets')).toBeInTheDocument();
      expect(screen.getByText('filament presets')).toBeInTheDocument();
      expect(screen.getByText('process presets')).toBeInTheDocument();
    });

    it('displays all printer presets with details', async () => {
      await uploadAndPreview();

      expect(screen.getByText('Bambu Lab X1 Carbon')).toBeInTheDocument();
      expect(screen.getByText(/Bambu Lab • 256x256x256mm • 0.4mm nozzle/)).toBeInTheDocument();
      expect(screen.getByText('Prusa MK4')).toBeInTheDocument();
      expect(screen.getByText(/Prusa Research • 250x210x220mm • 0.4mm nozzle/)).toBeInTheDocument();
    });

    it('displays all filament presets with details', async () => {
      await uploadAndPreview();

      expect(screen.getByText('Generic PLA')).toBeInTheDocument();
      expect(screen.getByText(/PLA • 215°C nozzle • 60°C bed/)).toBeInTheDocument();
      expect(screen.getByText('Generic PETG')).toBeInTheDocument();
      expect(screen.getByText(/PETG • 240°C nozzle • 80°C bed/)).toBeInTheDocument();
    });

    it('displays all process presets with details', async () => {
      await uploadAndPreview();

      expect(screen.getByText('0.20mm SPEED @BBL X1C')).toBeInTheDocument();
      expect(screen.getByText(/0.2mm layer • 15% infill • Standard quality/)).toBeInTheDocument();
      expect(screen.getByText('0.12mm FINE @BBL X1C')).toBeInTheDocument();
      expect(screen.getByText(/0.12mm layer • 20% infill • Fine quality/)).toBeInTheDocument();
    });

    it('selects all presets by default', async () => {
      await uploadAndPreview();

      const printerCheckboxes = screen.getAllByRole('checkbox').filter((cb) => {
        const label = cb.closest('label');
        return label?.textContent?.includes('Bambu Lab') || label?.textContent?.includes('Prusa');
      });

      printerCheckboxes.forEach((checkbox) => {
        expect(checkbox).toBeChecked();
      });
    });

    it('allows toggling individual preset selection', async () => {
      await uploadAndPreview();

      const x1Checkbox = screen
        .getAllByRole('checkbox')
        .find(
          (cb) => cb.closest('label')?.textContent?.includes('Bambu Lab X1 Carbon')
        ) as HTMLInputElement;

      expect(x1Checkbox).toBeChecked();

      await user.click(x1Checkbox);

      expect(x1Checkbox).not.toBeChecked();
    });

    it('allows selecting/deselecting all presets with category checkbox', async () => {
      await uploadAndPreview();

      const printerSelectAllCheckbox = screen.getByLabelText(/Select all printer presets/i);

      await user.click(printerSelectAllCheckbox); // Deselect all

      await waitFor(() => {
        expect(printerSelectAllCheckbox).not.toBeChecked();
      });

      await user.click(printerSelectAllCheckbox); // Select all again

      await waitFor(() => {
        expect(printerSelectAllCheckbox).toBeChecked();
      });
    });

    it('disables import button when no presets are selected', async () => {
      await uploadAndPreview();

      // Deselect all categories
      const selectAllCheckboxes = [
        screen.getByLabelText(/Select all printer presets/i),
        screen.getByLabelText(/Select all filament presets/i),
        screen.getByLabelText(/Select all process presets/i),
      ];

      for (const checkbox of selectAllCheckboxes) {
        await user.click(checkbox);
      }

      const importButton = screen.getByRole('button', { name: /Import Selected/i });

      expect(importButton).toBeDisabled();
    });

    it('navigates back to upload step', async () => {
      await uploadAndPreview();

      const backButton = screen.getByRole('button', { name: /Back/i });
      await user.click(backButton);

      await waitFor(() => {
        expect(screen.getByText('Upload OrcaSlicer Bundle')).toBeInTheDocument();
      });
    });
  });

  describe('Import Flow', () => {
    beforeEach(async () => {
      vi.mocked(orcaProfilesService.previewBundle).mockResolvedValue(mockPreview);
      vi.mocked(orcaProfilesService.importBundle).mockResolvedValue({
        success: true,
          printersImported: 2,
          filamentsImported: 2,
          processesImported: 2,
          warnings: [],
          errors: [],
      });
    });

    const uploadPreviewAndImport = async () => {
      renderWizard();

      const file = new File([mockBundleJson], 'bundle.json', {
        type: 'application/json',
      });
      const input = document.querySelector("#bundle-upload")! as HTMLInputElement;

      await user.upload(input, file);
      const previewButton = await screen.findByRole('button', { name: /Preview Bundle/i });
      await user.click(previewButton);

      await waitFor(() => {
        expect(screen.getByText('Bundle Preview')).toBeInTheDocument();
      });

      const importButton = screen.getByRole('button', { name: /Import Selected/i });
      await user.click(importButton);
    };

    it('successfully imports all selected presets', async () => {
      await uploadPreviewAndImport();

      await waitFor(() => {
        expect(orcaProfilesService.importBundle).toHaveBeenCalledWith({
          bundleJson: mockBundleJson,
          importPrinters: true,
          importFilaments: true,
          importProcesses: true,
        });
        expect(screen.getByText('Import Complete!')).toBeInTheDocument();
      });
    });

    it('displays success summary with import counts', async () => {
      await uploadPreviewAndImport();

      await waitFor(() => {
        expect(screen.getByText('Import Complete!')).toBeInTheDocument();
        // Should show counts for each category
        const counts = screen.getAllByText('2');
        expect(counts.length).toBeGreaterThanOrEqual(3); // At least 3 "2"s for the counts
      });
    });

    it('displays error message on import failure', async () => {
      vi.mocked(orcaProfilesService.importBundle).mockRejectedValue(
        new Error('Database error')
      );

      renderWizard();

      const file = new File([mockBundleJson], 'bundle.json', {
        type: 'application/json',
      });
      const input = document.querySelector("#bundle-upload")! as HTMLInputElement;

      await user.upload(input, file);
      const previewButton = await screen.findByRole('button', { name: /Preview Bundle/i });
      await user.click(previewButton);

      await waitFor(() => {
        expect(screen.getByText('Bundle Preview')).toBeInTheDocument();
      });

      const importButton = screen.getByRole('button', { name: /Import Selected/i });
      await user.click(importButton);

      await waitFor(() => {
        expect(screen.getByText('Import failed')).toBeInTheDocument();
        expect(screen.getByText('Database error')).toBeInTheDocument();
      });
    });

    it('allows importing another bundle from completion screen', async () => {
      await uploadPreviewAndImport();

      await waitFor(() => {
        expect(screen.getByText('Import Complete!')).toBeInTheDocument();
      });

      const importAnotherButton = screen.getByRole('button', {
        name: /Import Another Bundle/i,
      });
      await user.click(importAnotherButton);

      await waitFor(() => {
        expect(screen.getByText('Upload OrcaSlicer Bundle')).toBeInTheDocument();
      });
    });
  });

  describe('Step Indicator', () => {
    it('shows correct step progression', async () => {
      vi.mocked(orcaProfilesService.previewBundle).mockResolvedValue(mockPreview);

      renderWizard();

      // Initially on Upload step (step 1)
      expect(screen.getByText('Upload')).toBeInTheDocument();

      const file = new File([mockBundleJson], 'bundle.json', {
        type: 'application/json',
      });
      const input = document.querySelector("#bundle-upload")! as HTMLInputElement;

      await user.upload(input, file);
      const previewButton = await screen.findByRole('button', { name: /Preview Bundle/i });
      await user.click(previewButton);

      // Now on Preview step (step 2)
      await waitFor(() => {
        expect(screen.getByText('Preview')).toBeInTheDocument();
      });
    });
  });
});
