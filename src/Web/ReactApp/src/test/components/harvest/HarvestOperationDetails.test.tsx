
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { HarvestOperationDetails } from '@/components/harvest/HarvestOperationDetails';
import { GcodeHarvestOperation, GcodeHarvestStatus } from '@/types/api';
import * as api from '@/services/api';
import React from 'react';

vi.mock('@/services/api', async () => {
  const actual = await vi.importActual<typeof api>(
    '@/services/api'
  );
  return {
    ...actual,
    apiClient: {
      ...actual.apiClient,
      getGcodeFilesWithFilter: vi.fn(),
      getDiscoveredGcodeFiles: vi.fn().mockResolvedValue([
        {
          id: 'file-1',
          fileName: 'file1.gcode',
          filePath: '/printer/file1.gcode',
          size: 1024,
          modifiedAt: new Date().toISOString(),
          status: 'completed',
        },
        {
          id: 'file-2',
          fileName: 'file2.gcode',
          filePath: '/printer/file2.gcode',
          size: 2048,
          modifiedAt: new Date().toISOString(),
          status: 'error',
          error: 'Checksum failed',
        },
      ]),
    },
  };
});

const mockOperation: GcodeHarvestOperation = {
  id: 'op-1',
  printerId: 'printer-1',
  printerName: 'Test Printer',
  startedAt: new Date().toISOString(),
  completedAt: new Date().toISOString(),
  status: GcodeHarvestStatus.Completed,
  error: undefined,
  filesFound: 2,
  filesProcessed: 2,
  filesAdded: 1,
  filesSkipped: 0,
  filesErrored: 1,
  totalSizeBytes: 123456,
  options: {
    includeSubfolders: true,
    fileTypes: ['gcode'],
    minFileSize: 0,
    duplicateHandling: 'skip',
  },
  filesPaths: [],
  duplicatesSkipped: 0,
};

describe('HarvestOperationDetails', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders operation summary and discovered files', async () => {
    // Mock API for IndexedFilesList
    (api.apiClient.getGcodeFilesWithFilter as unknown as ReturnType<typeof vi.fn>).mockResolvedValue({
      files: [
        {
          id: 'file-1',
          name: 'file1.gcode',
          path: '/printer/file1.gcode',
          size: 1024,
          modifiedAt: new Date().toISOString(),
          status: 'completed',
        },
        {
          id: 'file-2',
          name: 'file2.gcode',
          path: '/printer/file2.gcode',
          size: 2048,
          modifiedAt: new Date().toISOString(),
          status: 'error',
          error: 'Checksum failed',
        },
      ],
    });

    render(
      <HarvestOperationDetails operation={mockOperation} onClose={() => {}} />
    );

    // Summary
    expect(screen.getByText('Operation Summary')).toBeInTheDocument();
    expect(screen.getByText('Test Printer')).toBeInTheDocument();
    expect(screen.getByText('Files Found:')).toBeInTheDocument();
    expect(screen.getByText('2')).toBeInTheDocument();

    // Discovered Files section
    expect(screen.getByText('Discovered Files')).toBeInTheDocument();
    expect(screen.getByText('You can retry or skip errored files, or import selected files to the library. This list is available for review even after completion or cancellation.')).toBeInTheDocument();

    // Wait for file rows
    await waitFor(() => {
      expect(screen.getByText('file1.gcode')).toBeInTheDocument();
      expect(screen.getByText('file2.gcode')).toBeInTheDocument();
      expect(screen.getByText('Checksum failed')).toBeInTheDocument();
    });
  });

  it('calls onClose when close button is clicked', () => {
    const onClose = vi.fn();
    render(<HarvestOperationDetails operation={mockOperation} onClose={onClose} />);
    fireEvent.click(screen.getByLabelText('Close details'));
    expect(onClose).toHaveBeenCalled();
  });
});
