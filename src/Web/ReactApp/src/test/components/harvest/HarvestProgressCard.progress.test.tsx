import React from 'react';
import { render } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { waitFor } from '@testing-library/react';
import { HarvestProgressCard } from '@/components/harvest/HarvestProgressCard';
import { AuthProvider } from '@/common/contexts/AuthContext';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

const queryClient = new QueryClient();
import { GcodeHarvestOperation, GcodeHarvestStatus } from '@/types/api';

describe('HarvestProgressCard progress widths', () => {
  it('applies exact percent widths for per-file progress bars', async () => {
    const op: GcodeHarvestOperation = {
      id: 'op-pf',
      printerId: 'p1',
      printerName: 'P1',
      startedAt: new Date().toISOString(),
      completedAt: undefined,
      status: GcodeHarvestStatus.Running,
      error: undefined,
      filesFound: 3,
      filesProcessed: 1,
      filesAdded: 0,
      filesSkipped: 0,
      filesErrored: 0,
      totalSizeBytes: 0,
      options: { includeSubfolders: true, fileTypes: ['gcode'], minFileSize: 0, duplicateHandling: 'skip' },
      filesPaths: [],
      duplicatesSkipped: 0,
    };

    const perFile = {
      'a.gcode': { fileName: 'a.gcode', percent: 12.7, status: 'processing' as const },
      'b.gcode': { fileName: 'b.gcode', percent: 100, status: 'completed' as const }
    };

    const { container } = render(
      <QueryClientProvider client={queryClient}>
        <AuthProvider>
          <HarvestProgressCard operation={op} perFileProgress={perFile} />
        </AuthProvider>
      </QueryClientProvider>
    );

    await waitFor(() => {
      // the per-file inner bars get their style.width set via refs; confirm one exists
      const bars = Array.from(container.querySelectorAll('div'))
        .filter(el => el instanceof HTMLElement && el.style && el.style.width);
      // ensure at least two found (main progress + per-file)
      expect(bars.length).toBeGreaterThanOrEqual(2);
      // find a bar for 12.7% specifically
      const found = bars.some((b: Element) => (b as HTMLElement).style.width === '12.7%');
      expect(found).toBe(true);
    });
  });
});
