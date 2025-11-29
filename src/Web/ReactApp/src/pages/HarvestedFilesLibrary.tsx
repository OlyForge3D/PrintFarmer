import React, { useEffect, useState } from 'react';
import { apiClient } from '@/services/api';
import { GcodeFile } from '@/types/api';
import { PageTemplate } from '@/components/PageTemplate';
import { Input, Select, FormField, Alert } from '@/components/ui';
import { FolderOpen } from 'lucide-react';

export const HarvestedFilesLibrary: React.FC = () => {
  const [files, setFiles] = useState<GcodeFile[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [sortBy, setSortBy] = useState<'name' | 'size' | 'date'>('date');
  const [sortOrder, setSortOrder] = useState<'asc' | 'desc'>('desc');

  useEffect(() => {
    setLoading(true);
    apiClient.getGcodeFilesWithFilter({
      source: 1, // 1 = Harvest (see GcodeSource enum)
      search: search || undefined,
      sortBy,
      sortOrder
    })
      .then(res => {
        setFiles(res.files);
        setError(null);
      })
      .catch(e => setError(e.message || 'Failed to load files'))
        .catch((e: Error) => setError(e.message || 'Failed to load files'))
      .finally(() => setLoading(false));
  }, [search, sortBy, sortOrder]);

  return (
    <PageTemplate
      title="Harvested G-code Files"
      subtitle="Browse and manage G-code files collected from your printers"
      icon={FolderOpen}
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
          <Select value={sortBy} onChange={e => setSortBy(e.target.value as 'name' | 'size' | 'date')}>
            <option value="name">Name</option>
            <option value="size">Size</option>
            <option value="date">Date</option>
          </Select>
        </FormField>
        <FormField label="Order">
          <Select value={sortOrder} onChange={e => setSortOrder(e.target.value as 'asc' | 'desc')}>
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
                <td className="p-2 border">{file.name || file.displayName || file.originalFileName}</td>
                <td className="p-2 border">{((file.size ?? file.fileSizeBytes) / 1024).toFixed(1)} KB</td>
                <td className="p-2 border">{new Date(file.modifiedAt ?? file.uploadedAt).toLocaleString()}</td>
                <td className="p-2 border">{file.sourcePrinterName || file.sourcePrinterId || '-'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </PageTemplate>
  );
};
