import React from 'react';
import { createRoot } from 'react-dom/client';
import { MemoryRouter } from 'react-router';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AuthContext } from '@/common/contexts/auth-context';
import { apiClient } from '@/services/api';
import { FilesPage } from '@/features/files/pages/FilesPage';

export function mountFilesPage(source) {
  apiClient.getUnifiedFiles = async () => ({
    items: [{
      source: 'Model', id: 'model', path: '/', name: 'part.stl',
      fileName: 'part.stl', fileSize: 10, fileType: 'stl',
      uploadedAt: '2026-10-28T00:00:00Z', url: '/api/3d-models/file/model',
    }],
    page: 1, pageSize: 50, totalPages: 1, totalItems: 1, totalSize: 10,
  });
  apiClient.listModelsFolders = async () => [{ path: '/Parts', fileName: 'Parts', isDirectory: true }];
  apiClient.getGcodeFilesFolders = async () => [];
  apiClient.getPrinters = async () => [{ id: 'source', name: 'Source printer', isOnline: true, isEnabled: true }];
  apiClient.getPrinterFileCatalog = async () => ({
    items: [], total: 0, page: 1, pageSize: 25, scans: [],
    printers: [{ id: 'source', name: 'Source printer', canManage: true }],
    automaticDiscoveryEnabled: true,
  });
  apiClient.getPrinterFileOperations = async () => [];
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  createRoot(document.getElementById('files-toolbar-root')).render(
    React.createElement(QueryClientProvider, { client: queryClient },
      React.createElement(AuthContext.Provider, {
        value: { hasPermission: (resource) => resource !== 'system_settings' },
      }, React.createElement(MemoryRouter, {
        initialEntries: [source === 'printers' ? '/files?source=printers' : '/files'],
      }, React.createElement(FilesPage))),
    ),
  );
}
