import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';

const toastErrorMock = vi.fn();
const toastSuccessMock = vi.fn();

// Mock the API client
vi.mock('../../../services/api', () => ({
  apiClient: {
    getSystemLogs: vi.fn().mockResolvedValue([]),
    getSystemLogsQuery: vi.fn().mockResolvedValue([]),
    exportSystemLogs: vi.fn().mockResolvedValue(new Blob(['[]'], { type: 'application/json' })),
  }
}));

// Mock the shared admin toast so the search/export failure paths can be asserted
// without depending on the real sonner runtime.
vi.mock('@/common/components/admin', async () => {
  const actual = await vi.importActual<typeof import('@/common/components/admin')>(
    '@/common/components/admin',
  );
  return {
    ...actual,
    adminToast: {
      success: (msg: string) => toastSuccessMock(msg),
      error: (msg: string) => toastErrorMock(msg),
      info: vi.fn(),
      warning: vi.fn(),
    },
  };
});

// Import after mocks are set up
import { SystemLogsContent } from '../../../features/admin/components/SystemLogsContent';
import { apiClient } from '../../../services/api';

describe('SystemLogsContent', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
    toastErrorMock.mockClear();
    toastSuccessMock.mockClear();
  });

  describe('rendering', () => {
    it('renders filter mode buttons', () => {
      render(<SystemLogsContent />);
      expect(screen.getByRole('button', { name: /simple filter/i })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /advanced query/i })).toBeInTheDocument();
    });

    it('renders simple filter fields by default', () => {
      render(<SystemLogsContent />);
      // Use getAllByText since "CorrelationId" appears in multiple places (label, column header, etc.)
      expect(screen.getAllByText(/correlationid/i).length).toBeGreaterThan(0);
      expect(screen.getAllByText(/level/i).length).toBeGreaterThan(0);
    });

    it('renders search and export buttons', () => {
      render(<SystemLogsContent />);
      expect(screen.getByRole('button', { name: /search/i })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /export/i })).toBeInTheDocument();
    });

    it('renders column visibility controls', () => {
      render(<SystemLogsContent />);
      expect(screen.getByText(/visible columns/i)).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /reset to defaults/i })).toBeInTheDocument();
    });

    it('renders empty state when no logs', () => {
      render(<SystemLogsContent />);
      expect(screen.getByText(/no logs found/i)).toBeInTheDocument();
    });
  });

  describe('filter mode switching', () => {
    it('switches to advanced query mode when clicked', () => {
      render(<SystemLogsContent />);
      
      fireEvent.click(screen.getByRole('button', { name: /advanced query/i }));
      
      expect(screen.getByText(/lucene query/i)).toBeInTheDocument();
      expect(screen.getByText(/supported fields/i)).toBeInTheDocument();
    });

    it('switches back to simple filter mode', () => {
      render(<SystemLogsContent />);
      
      fireEvent.click(screen.getByRole('button', { name: /advanced query/i }));
      fireEvent.click(screen.getByRole('button', { name: /simple filter/i }));
      
      expect(screen.queryByText(/lucene query/i)).not.toBeInTheDocument();
    });
  });

  describe('search functionality', () => {
    it('calls API when search button is clicked', async () => {
      render(<SystemLogsContent />);
      
      fireEvent.click(screen.getByRole('button', { name: /search/i }));
      
      await waitFor(() => {
        expect(apiClient.getSystemLogs).toHaveBeenCalled();
      });
    });

    it('uses advanced query API when in advanced mode', async () => {
      render(<SystemLogsContent />);
      
      fireEvent.click(screen.getByRole('button', { name: /advanced query/i }));
      fireEvent.click(screen.getByRole('button', { name: /search/i }));
      
      await waitFor(() => {
        expect(apiClient.getSystemLogsQuery).toHaveBeenCalled();
      });
    });
  });

  describe('column visibility', () => {
    it('saves column preferences to localStorage', () => {
      render(<SystemLogsContent />);
      
      const checkboxes = screen.getAllByRole('checkbox');
      fireEvent.click(checkboxes[0]); // Toggle first column
      
      expect(localStorage.getItem('logs-page-columns')).toBeTruthy();
    });

    it('resets columns to defaults', () => {
      localStorage.setItem('logs-page-columns', JSON.stringify({ timestamp: false }));
      
      render(<SystemLogsContent />);
      fireEvent.click(screen.getByRole('button', { name: /reset to defaults/i }));
      
      const saved = JSON.parse(localStorage.getItem('logs-page-columns') || '{}');
      expect(saved.timestamp).toBe(true);
    });
  });

  describe('accessibility', () => {
    it('has accessible table structure', () => {
      render(<SystemLogsContent />);
      expect(screen.getByRole('table')).toBeInTheDocument();
    });

    it('expand buttons have aria-labels', async () => {
      vi.mocked(apiClient.getSystemLogs).mockResolvedValueOnce([
        { id: 1, timestamp: new Date().toISOString(), level: 'Info', message: 'Test', correlationId: '123', source: 'test' }
      ]);
      
      render(<SystemLogsContent />);
      fireEvent.click(screen.getByRole('button', { name: /search/i }));
      
      await waitFor(() => {
        const expandButton = screen.getByLabelText(/expand row/i);
        expect(expandButton).toBeInTheDocument();
      });
    });
  });

  // Regression: the search and export paths previously used `window.alert()`, which
  // is unstyled, blocks the UI thread, and cannot be dismissed except via the OS
  // dialog. They should surface failures through `adminToast.error` (the same
  // primitive `SettingsPage` uses) and must never call `window.alert()`.
  describe('failure feedback (issue #943)', () => {
    it('surfaces search failures via adminToast.error and does not call window.alert', async () => {
      const alertSpy = vi.spyOn(window, 'alert').mockImplementation(() => {});
      vi.mocked(apiClient.getSystemLogs).mockRejectedValueOnce(new Error('boom-search'));

      render(<SystemLogsContent />);
      fireEvent.click(screen.getByRole('button', { name: /search/i }));

      await waitFor(() => {
        expect(toastErrorMock).toHaveBeenCalledWith('boom-search');
      });
      expect(alertSpy).not.toHaveBeenCalled();

      alertSpy.mockRestore();
    });

    it('surfaces advanced-query failures via adminToast.error', async () => {
      const alertSpy = vi.spyOn(window, 'alert').mockImplementation(() => {});
      vi.mocked(apiClient.getSystemLogsQuery).mockRejectedValueOnce(new Error('boom-query'));

      render(<SystemLogsContent />);
      fireEvent.click(screen.getByRole('button', { name: /advanced query/i }));
      fireEvent.click(screen.getByRole('button', { name: /search/i }));

      await waitFor(() => {
        expect(toastErrorMock).toHaveBeenCalledWith('boom-query');
      });
      expect(alertSpy).not.toHaveBeenCalled();

      alertSpy.mockRestore();
    });

    it('surfaces export failures via adminToast.error and does not call window.alert', async () => {
      const alertSpy = vi.spyOn(window, 'alert').mockImplementation(() => {});
      vi.mocked(apiClient.exportSystemLogs).mockRejectedValueOnce(new Error('boom-export'));

      render(<SystemLogsContent />);
      fireEvent.click(screen.getByRole('button', { name: /export/i }));

      await waitFor(() => {
        expect(toastErrorMock).toHaveBeenCalledWith('boom-export');
      });
      expect(alertSpy).not.toHaveBeenCalled();

      alertSpy.mockRestore();
    });

    it('uses a safe fallback message when the thrown value is not an Error', async () => {
      vi.mocked(apiClient.exportSystemLogs).mockRejectedValueOnce('not-an-error');

      render(<SystemLogsContent />);
      fireEvent.click(screen.getByRole('button', { name: /export/i }));

      await waitFor(() => {
        expect(toastErrorMock).toHaveBeenCalledWith('Failed to export logs');
      });
    });
  });
});
