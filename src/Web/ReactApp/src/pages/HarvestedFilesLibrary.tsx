import React, { useEffect, useState } from 'react';
import { apiClient } from '@/services/api';
import { GcodeFile } from '@/types/api';
import { PageTemplate } from '@/components/PageTemplate';
import { Input, Select, FormField, Alert } from '@/components/ui';
import { FolderIcon } from '@/components/icons/MdiIcons';

export const HarvestedFilesLibrary: React.FC = () => {
  const [files, setFiles] = useState<GcodeFile[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [sortBy, setSortBy] = useState<'name' | 'size' | 'date'>('date');
  const [sortOrder, setSortOrder] = useState<'asc' | 'desc'>('desc');

  useEffect(() => {
    setLoading(true);
    apiClient.queryGcodeLibrary(search || undefined)
      .then(allFiles => {
        // Filter for harvested files only (source = 1)
        const harvestedFiles = allFiles.filter(f => f.source === 1);
        
        // Apply sorting
        const sorted = [...harvestedFiles].sort((a, b) => {
          if (sortBy === 'name') {
            const aName = a.displayName || a.originalFileName || '';
            const bName = b.displayName || b.originalFileName || '';
            return sortOrder === 'asc' 
              ? aName.localeCompare(bName)
              : bName.localeCompare(aName);
          } else if (sortBy === 'size') {
            return sortOrder === 'asc'
              ? a.fileSizeBytes - b.fileSizeBytes
              : b.fileSizeBytes - a.fileSizeBytes;
          } else { // date
            const aDate = new Date(a.uploadedAt).getTime();
            const bDate = new Date(b.uploadedAt).getTime();
            return sortOrder === 'asc' ? aDate - bDate : bDate - aDate;
          }
        });
        
        setFiles(sorted);
        setError(null);
      })
      .catch((e: Error) => setError(e.message || 'Failed to load files'))
      .finally(() => setLoading(false));
  }, [search, sortBy, sortOrder]);

  return (
    <PageTemplate
      title="Harvested G-code Files"
      subtitle="Browse and manage G-code files collected from your printers"
      icon={FolderIcon}
      maxWidth="max-w-7xl"
    >
      <div className="flex flex-wrap gap-4 mb-4 items-end">
        <FormField label="Search" className="flex-1">
          <Input
            type="text"
            placeholder="Search files..."
            value={search}
            onChange={e => setSearch(e.target.value)}
          />
        </FormField>
        <FormField label="Sort by">
          <Select
            value={sortBy}
            onChange={e => setSortBy(e.target.value as 'name' | 'size' | 'date')}
          >
            <option value="name">Name</option>
            <option value="size">Size</option>
            <option value="date">Date</option>
          </Select>
        </FormField>
        <FormField label="Order">
          <Select
            value={sortOrder}
            onChange={e => setSortOrder(e.target.value as 'asc' | 'desc')}
          >
            <option value="desc">Newest</option>
            <option value="asc">Oldest</option>
          </Select>
        </FormField>
      </div>
      {loading ? (
        <div>Loading files...</div>
      ) : error ? (
        <Alert type="error" title="Error">
          {error}
        </Alert>
      ) : files.length === 0 ? (
        <div>No harvested files found.</div>
      ) : (
        <table className="min-w-full text-sm border">
          <thead>
            <tr>
              <th className="p-2 border">Name</th>
              <th className="p-2 border">Size</th>
              <th className="p-2 border">Modified</th>
              <th className="p-2 border">Source Printer</th>
            </tr>
          </thead>
          <tbody>
            {files.map(file => (
              <tr key={file.id}>
                <td className="p-2 border">{file.displayName || file.originalFileName}</td>
                <td className="p-2 border">{(file.fileSizeBytes / 1024).toFixed(1)} KB</td>
                <td className="p-2 border">{new Date(file.uploadedAt).toLocaleString()}</td>
                <td className="p-2 border">{file.sourcePrinterName || file.sourcePrinterId || '-'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </PageTemplate>
  );
};
