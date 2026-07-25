import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

/**
 * Epic #939 — secret masking must survive every new surface introduced by
 * Essential mode (#937), the page filter (#937), and palette deep-links
 * (#938). If any of those paths ever swap `SettingsPagelet` for a bespoke
 * renderer, we lose `type="password"` and every secret becomes plaintext.
 *
 * These tests exercise the masking on:
 *   - Everything mode (baseline)
 *   - Essential mode (Section rendered because a peer property is essential)
 *   - Page-filter search results
 *   - `?field=` deep-link
 */

const saveSettingsMock = vi.fn();
const scrollIntoViewMock = vi.fn();

vi.mock('@/services/settingsApi', async () => {
  return {
    fetchSettingsMetadata: vi.fn().mockResolvedValue([
      {
        // ObicoSettings is in the essential manifest for `enabled` and
        // `serverUrl`. `apiKey` is advanced.
        key: 'ObicoSettings',
        className: 'ObicoSettings',
        displayName: 'Obico Integration',
        description: 'Obico print monitoring.',
        group: 'Integrations',
        order: 1,
        properties: [
          {
            name: 'enabled',
            type: 'Boolean',
            attributes: [],
            display: { name: 'Obico Enabled', inputType: 'Boolean' },
          },
          {
            name: 'serverUrl',
            type: 'string',
            attributes: [],
            display: { name: 'Server URL', inputType: 'Url' },
          },
          {
            name: 'apiKey',
            type: 'string',
            attributes: [],
            display: {
              name: 'API Key',
              inputType: 'Password',
              description: 'Server-issued API key. Kept as a write-only secret.',
            },
          },
        ],
      },
    ]),
    fetchSettingsGroups: vi.fn().mockResolvedValue([
      { key: 'Integrations', displayName: 'Integrations', order: 1 },
    ]),
    fetchSettingsUnified: vi.fn().mockResolvedValue({
      ObicoSettings: {
        enabled: true,
        serverUrl: 'https://obico.example',
        apiKey: 'sk-topsecret-1234',
      },
    }),
    saveSettingsValues: (...args: unknown[]) => saveSettingsMock(...args),
    saveAllSettings: vi.fn(),
  };
});

vi.mock('@/hooks/useSlicer', () => ({
  useSlicer: () => ({ isSlicerAvailable: true, workerCount: 1 }),
}));
vi.mock('@/common/hooks/usePageTour', () => ({
  usePageTour: () => ({ startTour: vi.fn(), hasSeenTour: true, resetTour: vi.fn() }),
}));
vi.mock('@/features/admin/tours/settings.tour', () => ({ settingsTour: [] }));
vi.mock('@/features/admin/components/ObicoServersSection', () => ({
  ObicoServersSection: () => React.createElement('div', null, 'ObicoServersMock'),
}));
vi.mock('@/features/admin/components/FailureDetectionStatusCard', () => ({
  FailureDetectionStatusCard: () => React.createElement('div', null, 'FailureDetectionMock'),
}));

import { SettingsPage } from '@/features/admin/pages/SettingsPage';

async function renderPage(initialEntry = '/') {
  const result = render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <SettingsPage />
    </MemoryRouter>,
  );
  await waitFor(() => {
    expect(screen.getByTestId('settings-mode-controls')).toBeInTheDocument();
  });
  return result;
}

describe('SettingsPage — secret masking on every surface (#939)', () => {
  beforeEach(() => {
    saveSettingsMock.mockReset().mockResolvedValue(undefined);
    scrollIntoViewMock.mockReset();
    Element.prototype.scrollIntoView = scrollIntoViewMock;
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      value: vi.fn().mockImplementation(() => ({
        matches: false,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      })),
    });
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => {
      cb(performance.now());
      return 0;
    });
    window.localStorage.clear();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    window.localStorage.clear();
  });

  it('renders the Password input as type="password" in Everything mode', async () => {
    window.localStorage.setItem('pf.settings.mode', 'everything');
    await renderPage();

    const apiKey = screen.getByLabelText('API Key') as HTMLInputElement;
    expect(apiKey.type).toBe('password');
    // Value is bound but never leaks in plaintext through any test-observable
    // channel — HTMLInputElement.value is bound but the browser masks the render.
    expect(apiKey.value).toBe('sk-topsecret-1234');
  });

  it('preserves masking when the Password field is revealed via search in Essential mode', async () => {
    // Default is Essential mode. In the essential manifest ObicoSettings.apiKey
    // is *not* essential, so it is hidden by default and must only surface
    // via search. When it does surface, the input must still be masked.
    await renderPage();

    expect(screen.queryByLabelText('API Key')).not.toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText('Filter fields on this page…'), {
      target: { value: 'api key' },
    });

    const apiKey = (await screen.findByLabelText('API Key')) as HTMLInputElement;
    expect(apiKey.type).toBe('password');
  });

  it('preserves masking when the Password field is reached via `?field=` deep-link', async () => {
    // Essential mode + deep-link forces Everything for this render and scrolls
    // the row into view. The rendered input must still mask its value.
    window.localStorage.setItem('pf.settings.mode', 'essential');
    await renderPage('/?field=ObicoSettings.apiKey');

    const apiKey = (await screen.findByLabelText('API Key')) as HTMLInputElement;
    expect(apiKey.type).toBe('password');

    // Sanity: the deep-link highlight fires on the correct row.
    const target = document.querySelector('[data-setting-property="ObicoSettings.apiKey"]');
    expect(target).toBeTruthy();
    await waitFor(() => {
      expect(target!.classList.contains('pf-setting-focus')).toBe(true);
    });
  });

  it('never renders a non-password `type` attribute on a Password-inputType property, regardless of view', async () => {
    // Regression clamp — if a future change routes Password properties
    // through a bespoke input path (say, a "reveal secret" affordance), the
    // fallback path in `SettingsPagelet` must still produce `type="password"`.
    // Iterate every mode + filter combination that could render the input.
    for (const mode of ['essential', 'everything'] as const) {
      window.localStorage.setItem('pf.settings.mode', mode);
      const { unmount } = render(
        <MemoryRouter>
          <SettingsPage />
        </MemoryRouter>,
      );
      await waitFor(() => {
        expect(screen.getByTestId('settings-mode-controls')).toBeInTheDocument();
      });

      // Force the apiKey field into the DOM via search.
      fireEvent.change(screen.getByPlaceholderText('Filter fields on this page…'), {
        target: { value: 'api key' },
      });
      const apiKey = (await screen.findByLabelText('API Key')) as HTMLInputElement;
      expect(apiKey.type).toBe('password');
      // No text-alike input types silently substituted.
      expect(['text', 'email', 'search']).not.toContain(apiKey.type);

      unmount();
    }
  });
});
