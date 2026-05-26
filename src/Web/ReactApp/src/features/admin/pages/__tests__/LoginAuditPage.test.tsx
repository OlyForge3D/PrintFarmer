import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { LoginAuditPage } from '@/features/admin/pages/LoginAuditPage';
import * as securityAuditService from '@/services/securityAuditService';

vi.mock('@/services/securityAuditService', () => ({
  fetchLoginAudit: vi.fn(),
}));

vi.mock('@/common/components/PageTemplate', () => ({
  PageTemplate: ({ title, subtitle, children }: { title: string; subtitle?: string; children: React.ReactNode }) => (
    <div data-testid="page-template">
      <h1>{title}</h1>
      {subtitle && <p>{subtitle}</p>}
      {children}
    </div>
  ),
}));

const mockEntry = {
  id: 'entry-1',
  timestamp: '2026-05-26T17:20:00Z',
  username: 'admin',
  success: true,
  ipAddress: '10.0.0.42',
  userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)',
  failureReason: null,
};

const mockFailedEntry = {
  id: 'entry-2',
  timestamp: '2026-05-26T17:21:00Z',
  username: 'badactor',
  success: false,
  ipAddress: '192.168.1.5',
  userAgent: 'curl/7.88.0',
  failureReason: 'Invalid credentials',
};

const mockPagedResponse = {
  items: [mockEntry, mockFailedEntry],
  totalCount: 2,
  page: 1,
  pageSize: 50,
};

function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false },
    },
  });
}

function renderPage(initialUrl = '/admin/security/login-audit') {
  const queryClient = createQueryClient();
  return render(
    <MemoryRouter initialEntries={[initialUrl]}>
      <QueryClientProvider client={queryClient}>
        <LoginAuditPage />
      </QueryClientProvider>
    </MemoryRouter>,
  );
}

describe('LoginAuditPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders page title and subtitle', async () => {
    vi.mocked(securityAuditService.fetchLoginAudit).mockResolvedValue(mockPagedResponse);

    renderPage();

    expect(screen.getByText('Login Audit Log')).toBeInTheDocument();
  });

  it('shows spinner while loading', () => {
    vi.mocked(securityAuditService.fetchLoginAudit).mockReturnValue(new Promise(() => {}));

    renderPage();

    expect(screen.getByRole('status', { name: /loading/i })).toBeInTheDocument();
  });

  it('renders filter inputs', async () => {
    vi.mocked(securityAuditService.fetchLoginAudit).mockResolvedValue(mockPagedResponse);

    renderPage();

    expect(screen.getByLabelText(/filter from date/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/filter to date/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/filter by username/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/filter by login status/i)).toBeInTheDocument();
  });

  it('renders table with audit entries after load', async () => {
    vi.mocked(securityAuditService.fetchLoginAudit).mockResolvedValue(mockPagedResponse);

    renderPage();

    await waitFor(() => {
      expect(screen.getByRole('table', { name: /login audit log/i })).toBeInTheDocument();
    });

    expect(screen.getByText('admin')).toBeInTheDocument();
    expect(screen.getByText('badactor')).toBeInTheDocument();
  });

  it('shows success badge for successful logins', async () => {
    vi.mocked(securityAuditService.fetchLoginAudit).mockResolvedValue(mockPagedResponse);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText('✅ Success')).toBeInTheDocument();
    });
  });

  it('shows error badge for failed logins', async () => {
    vi.mocked(securityAuditService.fetchLoginAudit).mockResolvedValue(mockPagedResponse);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText('❌ Failed')).toBeInTheDocument();
    });
  });

  it('displays failure reason for failed entries', async () => {
    vi.mocked(securityAuditService.fetchLoginAudit).mockResolvedValue(mockPagedResponse);

    renderPage();

    await waitFor(() => {
      expect(screen.getByText('Invalid credentials')).toBeInTheDocument();
    });
  });

  it('renders IP address in monospace', async () => {
    vi.mocked(securityAuditService.fetchLoginAudit).mockResolvedValue(mockPagedResponse);

    renderPage();

    await waitFor(() => {
      const ip = screen.getByText('10.0.0.42');
      expect(ip).toHaveClass('font-mono');
    });
  });

  it('truncates long user agent strings', async () => {
    const longUaEntry = { ...mockEntry, userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36' };
    vi.mocked(securityAuditService.fetchLoginAudit).mockResolvedValue({
      ...mockPagedResponse,
      items: [longUaEntry],
    });

    renderPage();

    await waitFor(() => {
      const truncated = screen.getByText(/Mozilla\/5\.0 \(Windows NT 10\.0;/);
      expect(truncated.textContent?.length).toBeLessThanOrEqual(32); // 30 chars + ellipsis
    });
  });

  it('shows empty state when no results', async () => {
    vi.mocked(securityAuditService.fetchLoginAudit).mockResolvedValue({
      items: [],
      totalCount: 0,
      page: 1,
      pageSize: 50,
    });

    renderPage();

    await waitFor(() => {
      expect(screen.getByText('No login attempts found')).toBeInTheDocument();
    });
  });

  it('shows error state on fetch failure', async () => {
    vi.mocked(securityAuditService.fetchLoginAudit).mockRejectedValue(new Error('Network error'));

    renderPage();

    await waitFor(() => {
      expect(screen.getByRole('alert')).toBeInTheDocument();
      expect(screen.getByText(/failed to load login audit log/i)).toBeInTheDocument();
    });
  });

  it('shows clear filters button when a filter is active', async () => {
    vi.mocked(securityAuditService.fetchLoginAudit).mockResolvedValue(mockPagedResponse);

    renderPage('/admin/security/login-audit?username=admin');

    await waitFor(() => {
      expect(screen.getByRole('button', { name: /clear all filters/i })).toBeInTheDocument();
    });
  });

  it('does not show clear filters button with no active filters', async () => {
    vi.mocked(securityAuditService.fetchLoginAudit).mockResolvedValue(mockPagedResponse);

    renderPage();

    await waitFor(() => {
      expect(screen.queryByRole('button', { name: /clear all filters/i })).not.toBeInTheDocument();
    });
  });

  it('status dropdown contains all three options', async () => {
    vi.mocked(securityAuditService.fetchLoginAudit).mockResolvedValue(mockPagedResponse);

    renderPage();

    const select = screen.getByLabelText(/filter by login status/i);
    expect(select).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'All attempts' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Successful' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Failed' })).toBeInTheDocument();
  });

  it('shows entry count summary when data is loaded', async () => {
    vi.mocked(securityAuditService.fetchLoginAudit).mockResolvedValue({
      ...mockPagedResponse,
      totalCount: 2,
    });

    renderPage();

    await waitFor(() => {
      expect(screen.getByText(/showing 1–2 of 2 entries/i)).toBeInTheDocument();
    });
  });
});
