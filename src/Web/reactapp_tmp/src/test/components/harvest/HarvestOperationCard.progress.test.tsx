import React from 'react';
import { render } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { waitFor } from '@testing-library/react';
import { HarvestOperationCard } from '@/features/gcode/components/harvest/HarvestOperationCard';
import { AuthProvider } from '@/common/contexts/AuthContext';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

const queryClient = new QueryClient();
import { GcodeHarvestOperation, GcodeHarvestStatus } from '@/types/api';

describe('HarvestOperationCard progress bar', () => {
  it('sets precise width on the inner progress element', async () => {
    const op: GcodeHarvestOperation = {
      id: 'op-test',
      printerId: 'p1',
      printerName: 'P1',
      startedAt: new Date().toISOString(),
      completedAt: undefined,
      status: GcodeHarvestStatus.Running,
      error: undefined,
      filesFound: 100,
      filesProcessed: 37,
      filesAdded: 0,
      filesSkipped: 0,
      filesErrored: 0,
      totalSizeBytes: 0,
      options: { includeSubfolders: true, fileTypes: ['gcode'], minFileSize: 0, duplicateHandling: 'skip' },
      filesPaths: [],
      duplicatesSkipped: 0,
    };

    const { container, getByText } = render(
      <QueryClientProvider client={queryClient}>
        <AuthProvider>
          <HarvestOperationCard operation={op} showProgress onViewDetails={undefined} />
        </AuthProvider>
      </QueryClientProvider>
    );

    // ensure percent text appears
    expect(getByText('37%')).toBeTruthy();

    await waitFor(() => {
      // find any element with a style attribute (the inner bar is updated via ref)
      const styled = container.querySelector('div[style]') as HTMLElement | null;
      expect(styled).not.toBeNull();
      expect(styled!.style.width).toBe('37%');
    });
  });
});
