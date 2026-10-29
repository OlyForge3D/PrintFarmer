import React, { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuthContext } from '@/common/contexts/auth-context';
import { PageTemplate } from '@/common/components/PageTemplate';
import { SettingsSidebar } from '@/features/settings/components/SettingsSidebar';
import { SETTINGS_CATEGORIES, SETTINGS_SCOPES } from '@/features/settings/types';
import { WorkspaceSearchResults } from '@/features/settings/components/WorkspaceSearchResults';

function SettingsFrame({ scope }) {
  const [selected, setSelected] = useState('');
  const scopes = SETTINGS_SCOPES.filter(item => item.id === scope);
  return React.createElement(MemoryRouter, null,
    React.createElement(PageTemplate, {
      title: scopes[0].label,
      fill: true,
      actions: scope === 'system' ? React.createElement(WorkspaceSearchResults, {
        initialQuery: '',
        onQueryCommit: () => {},
        onSelect: () => {},
        onOpenPalette: () => setSelected('palette'),
      }) : undefined,
    },
      React.createElement('div', {
        className: 'relative flex flex-1 min-h-0 flex-col overflow-hidden rounded-md border border-pf-border bg-pf-panel',
        'data-testid': 'settings-frame',
      }, React.createElement('div', { className: 'relative flex min-h-0 flex-1 flex-col' },
        React.createElement('div', { className: 'flex flex-1 min-h-0 flex-col md:grid md:grid-cols-[13.5rem_minmax(0,1fr)]' },
          React.createElement(SettingsSidebar, {
            categories: SETTINGS_CATEGORIES.filter(item => item.scopeId === scope),
            activeScope: scope,
            activeCategory: scope === 'system' ? 'general' : 'profile',
            activeSubPage: scope === 'system' ? 'farm' : 'preferences',
            availableScopes: scopes,
            onCategoryChange: setSelected,
            onSelectDestination: destination => setSelected(destination.path),
          }),
          React.createElement('div', { className: 'relative flex min-h-0 flex-1 flex-col border-t border-pf-border md:border-t-0 md:border-l' },
            React.createElement('div', { className: 'pf-settings-scroll-pane min-h-0 flex-1 overflow-y-auto overscroll-contain' },
              React.createElement('div', { className: 'h-[2000px] px-4 py-6' },
                React.createElement('output', { 'aria-label': 'Selected destination' }, selected),
              ),
            ),
          ),
        ),
      )),
    ),
  );
}

export function mountSettingsSidebar(scope) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  createRoot(document.getElementById('settings-sidebar-root')).render(
    React.createElement(QueryClientProvider, { client: queryClient },
      React.createElement(AuthContext.Provider, {
        value: { user: null, hasRole: () => false, hasPermission: () => false },
      }, React.createElement(SettingsFrame, { scope })),
    ),
  );
}
