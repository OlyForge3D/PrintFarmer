import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import type { ApplicationReleaseUpdateStatusDto } from '@/types/releaseUpdates';

const { mockGet, mockHasRole } = vi.hoisted(() => ({
  mockGet: vi.fn(),
  mockHasRole: vi.fn(),
}));

vi.mock('@/services/api/httpClient', () => ({
  client: { get: mockGet },
}));

vi.mock('@/features/auth/hooks/useAuth', () => ({
  useAuth: () => ({ hasRole: mockHasRole }),
}));

import { ReleaseUpdateBanner } from '../ReleaseUpdateBanner';

function status(overrides: Partial<ApplicationReleaseUpdateStatusDto> = {}): ApplicationReleaseUpdateStatusDto {
  return {
    status: 'UpdateAvailable',
    updateAvailable: true,
    installedVersion: '0.2.3-insider.5',
    channel: 'Insider',
    latestVersion: '0.2.3-insider.6',
    latestTag: 'v0.2.3-insider.6',
    latestReleaseName: 'v0.2.3-insider.6',
    latestPublishedAt: '2026-10-01T00:00:00Z',
    releaseUrl: 'https://github.com/OlyForge3D/PrintFarmer/releases/tag/v0.2.3-insider.6',
    lastCheckedAt: '2026-10-05T00:00:00Z',
    lastSuccessfulCheckAt: '2026-10-05T00:00:00Z',
    isStale: false,
    error: null,
    checkIntervalSeconds: 21600,
    upgradeDocsUrl:
      'https://github.com/OlyForge3D/PrintFarmer/blob/v0.2.3-insider.6/docs/DEPLOYMENT.md#upgrading-to-a-new-release',
    ...overrides,
  };
}

function renderBanner() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  );
  return render(<ReleaseUpdateBanner />, { wrapper });
}

describe('ReleaseUpdateBanner', () => {
  beforeEach(() => {
    mockGet.mockReset();
    mockHasRole.mockReset();
    sessionStorage.clear();
  });

  it('shows installed and available versions with upgrade guidance for farm admins', async () => {
    mockHasRole.mockImplementation((role: string) => role === 'farm_admin');
    mockGet.mockResolvedValue({ data: status() });

    renderBanner();

    expect(await screen.findByText('PrintFarmer 0.2.3-insider.6 is available')).toBeInTheDocument();
    expect(screen.getByText('0.2.3-insider.5')).toBeInTheDocument();
    expect(screen.getByText(/insider channel/)).toBeInTheDocument();
    expect(screen.getByText('./install.sh --upgrade --version 0.2.3-insider.6')).toBeInTheDocument();
    expect(mockGet).toHaveBeenCalledWith('/admin/release-updates', expect.anything());

    const notes = screen.getByRole('link', { name: /release notes for v0\.2\.3-insider\.6/i });
    expect(notes).toHaveAttribute('href', status().releaseUrl);
    expect(notes).toHaveAttribute('rel', 'noopener noreferrer');
    const guide = screen.getByRole('link', { name: /upgrade guide/i });
    expect(guide).toHaveAttribute('href', status().upgradeDocsUrl);
  });

  it('never requests release status for non-admins', async () => {
    mockHasRole.mockReturnValue(false);
    mockGet.mockResolvedValue({ data: status() });

    const { container } = renderBanner();

    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 20));
    });
    expect(container).toBeEmptyDOMElement();
    expect(mockGet).not.toHaveBeenCalled();
  });

  it.each([
    ['UpToDate', status({ status: 'UpToDate', updateAvailable: false })],
    ['Disabled', status({ status: 'Disabled', updateAvailable: false, latestVersion: null, latestTag: null })],
    [
      'UnknownInstalledVersion',
      status({ status: 'UnknownInstalledVersion', updateAvailable: false, installedVersion: null }),
    ],
    ['NotChecked', status({ status: 'NotChecked', updateAvailable: false, latestVersion: null })],
    ['CheckFailed without a known update', status({ status: 'CheckFailed', updateAvailable: false })],
  ])('renders nothing when status is %s', async (_label, dto) => {
    mockHasRole.mockReturnValue(true);
    mockGet.mockResolvedValue({ data: dto });

    const { container } = renderBanner();

    await waitFor(() => expect(mockGet).toHaveBeenCalled());
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 20));
    });
    expect(container).toBeEmptyDOMElement();
  });

  it('says honestly when the latest check failed', async () => {
    mockHasRole.mockReturnValue(true);
    mockGet.mockResolvedValue({ data: status({ status: 'CheckFailed', error: 'GitHub release check timed out.' }) });

    renderBanner();

    expect(await screen.findByText(/the latest release check failed; last successful check/i)).toBeInTheDocument();
  });

  it('flags stale release information', async () => {
    mockHasRole.mockReturnValue(true);
    mockGet.mockResolvedValue({ data: status({ isStale: true }) });

    renderBanner();

    expect(await screen.findByText(/release information may be out of date/i)).toBeInTheDocument();
  });

  it('omits links that do not point at the PrintFarmer repository', async () => {
    mockHasRole.mockReturnValue(true);
    mockGet.mockResolvedValue({
      data: status({ releaseUrl: 'https://evil.example/releases', upgradeDocsUrl: 'javascript:alert(1)' }),
    });

    renderBanner();

    await screen.findByText('PrintFarmer 0.2.3-insider.6 is available');
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });

  it('can be dismissed for the current release tag', async () => {
    mockHasRole.mockReturnValue(true);
    mockGet.mockResolvedValue({ data: status() });
    const user = userEvent.setup();

    renderBanner();

    await user.click(await screen.findByRole('button', { name: 'Dismiss message' }));

    expect(screen.queryByText('PrintFarmer 0.2.3-insider.6 is available')).not.toBeInTheDocument();
    expect(sessionStorage.getItem('pf.releaseUpdateBanner.dismissedTag')).toBe('v0.2.3-insider.6');
  });

  it('reappears when a newer tag than the dismissed one is published', async () => {
    sessionStorage.setItem('pf.releaseUpdateBanner.dismissedTag', 'v0.2.3-insider.6');
    mockHasRole.mockReturnValue(true);
    mockGet.mockResolvedValue({
      data: status({ latestVersion: '0.2.3-insider.7', latestTag: 'v0.2.3-insider.7' }),
    });

    renderBanner();

    expect(await screen.findByText('PrintFarmer 0.2.3-insider.7 is available')).toBeInTheDocument();
  });
});
