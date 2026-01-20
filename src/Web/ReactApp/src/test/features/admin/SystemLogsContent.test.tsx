import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';

// Mock the API client
vi.mock('../../../services/api', () => ({
  apiClient: {
    getSystemLogs: vi.fn().mockResolvedValue([]),
    getSystemLogsQuery: vi.fn().mockResolvedValue([]),
    exportSystemLogs: vi.fn().mockResolvedValue(new Blob(['[]'], { type: 'application/json' })),
  }
}));

// Import after mocks are set up
import { SystemLogsContent } from '../../../features/admin/components/SystemLogsContent';
import { apiClient } from '../../../services/api';

describe('SystemLogsContent', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
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
});
