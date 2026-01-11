import React, { useState, useEffect } from "react";
import axios from "axios";
import { Button, Input, FormField, Select, Textarea, Checkbox } from '@/common/components/ui';
import { PageTemplate } from '@/common/components/PageTemplate';
import type { SystemLog, LogColumnKey } from '@/types/admin';

const DEFAULT_COLUMNS: Record<LogColumnKey, { label: string; default: boolean }> = {
  timestamp: { label: 'Timestamp', default: true },
  level: { label: 'Level', default: true },
  message: { label: 'Message', default: true },
  correlationId: { label: 'CorrelationId', default: true },
  source: { label: 'Source', default: true },
  metadata: { label: 'Metadata', default: false },
  exception: { label: 'Exception', default: false },
};

const COLUMNS_STORAGE_KEY = 'logs-page-columns';

export function LogsPage() {
  const [logs, setLogs] = useState<SystemLog[]>([]);
  const [loading, setLoading] = useState(false);
  const [useAdvancedQuery, setUseAdvancedQuery] = useState(false);
  const [queryString, setQueryString] = useState("");
  const [visibleColumns, setVisibleColumns] = useState<Record<LogColumnKey, boolean>>(() => {
    const saved = localStorage.getItem(COLUMNS_STORAGE_KEY);
    if (saved) {
      try {
        return JSON.parse(saved);
      } catch {
        return Object.fromEntries(
          Object.entries(DEFAULT_COLUMNS).map(([key, { default: def }]) => [key, def])
        ) as Record<LogColumnKey, boolean>;
      }
    }
    return Object.fromEntries(
      Object.entries(DEFAULT_COLUMNS).map(([key, { default: def }]) => [key, def])
    ) as Record<LogColumnKey, boolean>;
  });
  const [expandedRowId, setExpandedRowId] = useState<number | null>(null);
  const [filters, setFilters] = useState({
    correlationId: "",
    level: "",
    from: "",
    to: "",
    metadata: ""
  });

  // Save column preferences to localStorage whenever they change
  useEffect(() => {
    localStorage.setItem(COLUMNS_STORAGE_KEY, JSON.stringify(visibleColumns));
  }, [visibleColumns]);

  const toggleColumn = (column: LogColumnKey) => {
    setVisibleColumns(prev => ({ ...prev, [column]: !prev[column] }));
  };

  const resetColumns = () => {
    const defaults = Object.fromEntries(
      Object.entries(DEFAULT_COLUMNS).map(([key, { default: def }]) => [key, def])
    ) as Record<LogColumnKey, boolean>;
    setVisibleColumns(defaults);
  };

  const fetchLogs = async () => {
    setLoading(true);
    try {
      let res;
      if (useAdvancedQuery) {
        res = await axios.get("/api/systemlogs/query", { params: { q: queryString } });
      } else {
        const params: Record<string, string> = {};
        if (filters.correlationId) params.correlationId = filters.correlationId;
        if (filters.level) params.level = filters.level;
        // Convert date strings (YYYY-MM-DD) to ISO 8601 datetime strings
        if (filters.from) params.from = `${filters.from}T00:00:00Z`;
        if (filters.to) params.to = `${filters.to}T23:59:59Z`;
        if (filters.metadata) params.metadata = filters.metadata;
        res = await axios.get("/api/systemlogs", { params });
      }
      setLogs(res.data);
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to fetch logs';
      alert(message);
    }
    setLoading(false);
  };

  const exportLogs = async () => {
    try {
      const params: Record<string, string> = {};
      if (filters.correlationId) params.correlationId = filters.correlationId;
      if (filters.level) params.level = filters.level;
      // Convert date strings (YYYY-MM-DD) to ISO 8601 datetime strings
      if (filters.from) params.from = `${filters.from}T00:00:00Z`;
      if (filters.to) params.to = `${filters.to}T23:59:59Z`;
      if (filters.metadata) params.metadata = filters.metadata;
      const res = await axios.get("/api/systemlogs/export", { params, responseType: "blob" });
      const url = window.URL.createObjectURL(new Blob([res.data]));
      const link = document.createElement("a");
      link.href = url;
      link.setAttribute("download", `systemlogs_export.json`);
      document.body.appendChild(link);
      link.click();
      link.remove();
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Failed to export logs';
      alert(message);
    }
  };

  const visibleColumnCount = Object.values(visibleColumns).filter(Boolean).length;

  return (
    <PageTemplate title="System Logs">
      {/* Mode Selection */}
      <div className="mb-4 flex gap-2">
        <Button 
          variant={useAdvancedQuery ? "secondary" : "primary"} 
          onClick={() => setUseAdvancedQuery(false)}
        >
          Simple Filter
        </Button>
        <Button 
          variant={useAdvancedQuery ? "primary" : "secondary"} 
          onClick={() => setUseAdvancedQuery(true)}
        >
          Advanced Query
        </Button>
      </div>

      {/* Filters */}
      {!useAdvancedQuery && (
        <div className="mb-4 grid grid-cols-1 md:grid-cols-5 gap-2">
          <FormField label="CorrelationId" inline>
            <Input placeholder="CorrelationId" value={filters.correlationId} onChange={e => setFilters(f => ({ ...f, correlationId: e.target.value }))} />
          </FormField>
          <FormField label="Level" inline>
            <Select value={filters.level} onChange={e => setFilters(f => ({ ...f, level: e.target.value }))}>
              <option value="">All Levels</option>
              <option value="Trace">Trace</option>
              <option value="Debug">Debug</option>
              <option value="Information">Information</option>
              <option value="Warning">Warning</option>
              <option value="Error">Error</option>
              <option value="Critical">Critical</option>
            </Select>
          </FormField>
          <FormField label="From" inline>
            <Input type="date" placeholder="From" value={filters.from} onChange={e => setFilters(f => ({ ...f, from: e.target.value }))} />
          </FormField>
          <FormField label="To" inline>
            <Input type="date" placeholder="To" value={filters.to} onChange={e => setFilters(f => ({ ...f, to: e.target.value }))} />
          </FormField>
          <FormField label="Metadata" inline>
            <Input placeholder="Metadata" value={filters.metadata} onChange={e => setFilters(f => ({ ...f, metadata: e.target.value }))} />
          </FormField>
        </div>
      )}

      {/* Advanced Query */}
      {useAdvancedQuery && (
        <div className="mb-4">
          <FormField label="Lucene Query" inline={false}>
            <Textarea
              value={queryString}
              onChange={e => setQueryString(e.target.value)}
              placeholder="Example: level:Error AND message:timeout"
              rows={4}
            />
          </FormField>
          <div className="mb-2 text-sm text-pf-text-muted">
            <p><strong>Supported fields:</strong> level, message, correlationId, source, metadata</p>
            <p><strong>Examples:</strong></p>
            <ul className="list-disc pl-5">
              <li>level:Error</li>
              <li>level:Error AND message:timeout</li>
              <li>correlationId:abc123</li>
              <li>message:"nozzle clog"</li>
            </ul>
          </div>
        </div>
      )}

      {/* Action Buttons */}
      <div className="mb-4 flex gap-2">
        <Button variant="primary" onClick={fetchLogs} disabled={loading}>Search</Button>
        <Button variant="success" onClick={exportLogs}>Export</Button>
      </div>

      {/* Column Visibility Controls */}
      <div className="mb-4 p-3 bg-pf-bg-1 rounded border border-pf-border">
        <div className="flex items-center justify-between mb-2">
          <h3 className="text-sm font-semibold text-pf-text-primary">Visible Columns</h3>
          <Button variant="secondary" size="sm" onClick={resetColumns}>Reset to Defaults</Button>
        </div>
          <div className="grid grid-cols-2 md:grid-cols-4 gap-2">
          {Object.entries(DEFAULT_COLUMNS).map(([key, { label }]) => (
            <label key={key} className="flex items-center gap-2 cursor-pointer">
              <Checkbox
                checked={visibleColumns[key as LogColumnKey]}
                onChange={() => toggleColumn(key as LogColumnKey)}
              />
              <span className="text-sm text-pf-text-secondary">{label}</span>
            </label>
          ))}
        </div>
      </div>

      {loading && <div className="text-pf-text-muted mb-4">Loading logs...</div>}

      {/* Logs Table */}
      <div className="overflow-x-auto border border-pf-border rounded">
        <table className="w-full">
          <thead>
            <tr className="bg-pf-bg-1 border-b border-pf-border">
              <th className="p-2 text-left text-xs font-semibold text-pf-text-primary">Expand</th>
              {visibleColumns.timestamp && <th className="p-2 text-left text-xs font-semibold text-pf-text-primary w-32">Timestamp</th>}
              {visibleColumns.level && <th className="p-2 text-left text-xs font-semibold text-pf-text-primary w-20">Level</th>}
              {visibleColumns.message && <th className="p-2 text-left text-xs font-semibold text-pf-text-primary flex-1 min-w-80">Message</th>}
              {visibleColumns.correlationId && <th className="p-2 text-left text-xs font-semibold text-pf-text-primary w-32">CorrelationId</th>}
              {visibleColumns.source && <th className="p-2 text-left text-xs font-semibold text-pf-text-primary w-32">Source</th>}
              {visibleColumns.metadata && <th className="p-2 text-left text-xs font-semibold text-pf-text-primary w-40">Metadata</th>}
              {visibleColumns.exception && <th className="p-2 text-left text-xs font-semibold text-pf-text-primary w-40">Exception</th>}
            </tr>
          </thead>
          <tbody>
            {logs.map((log, index) => (
              <React.Fragment key={log.id}>
                <tr className={index % 2 === 0 ? 'bg-pf-bg-0' : 'bg-pf-bg-1'} style={{borderBottom: '1px solid var(--pf-border)'}}>
                  <td className="p-2 text-center">
                    <Button
                      variant="subtle"
                      size="sm"
                      onClick={() => setExpandedRowId(expandedRowId === log.id ? null : log.id)}
                      className="text-pf-accent hover:text-pf-accent-hover"
                      aria-label={expandedRowId === log.id ? 'Collapse row' : 'Expand row'}
                    >
                      {expandedRowId === log.id ? '▼' : '▶'}
                    </Button>
                  </td>
                  {visibleColumns.timestamp && <td className="p-2 text-xs text-pf-text-secondary whitespace-nowrap">{new Date(log.timestamp).toLocaleString()}</td>}
                  {visibleColumns.level && <td className="p-2 text-xs text-pf-text-primary font-medium">{log.level}</td>}
                  {visibleColumns.message && <td className="p-2 text-xs text-pf-text-secondary line-clamp-2">{log.message}</td>}
                  {visibleColumns.correlationId && <td className="p-2 text-xs text-pf-text-secondary font-mono">{log.correlationId || '-'}</td>}
                  {visibleColumns.source && <td className="p-2 text-xs text-pf-text-secondary">{log.source || '-'}</td>}
                  {visibleColumns.metadata && <td className="p-2 text-xs text-pf-text-secondary truncate" title={log.metadata}>{log.metadata || '-'}</td>}
                  {visibleColumns.exception && <td className="p-2 text-xs text-pf-text-secondary truncate" title={log.exception}>{log.exception ? '✗ Error' : '-'}</td>}
                </tr>
                {/* Expanded Row */}
                {expandedRowId === log.id && (
                  <tr className="bg-pf-bg-2 border-t-2 border-pf-accent">
                    <td colSpan={1 + visibleColumnCount} className="p-4">
                      <div className="space-y-3">
                        {visibleColumns.message && (
                          <div>
                            <h4 className="text-xs font-semibold text-pf-text-primary mb-1">Full Message</h4>
                            <p className="text-xs text-pf-text-secondary bg-pf-bg-0 p-2 rounded whitespace-pre-wrap break-words font-mono">
                              {log.message}
                            </p>
                          </div>
                        )}
                        {visibleColumns.metadata && log.metadata && (
                          <div>
                            <h4 className="text-xs font-semibold text-pf-text-primary mb-1">Metadata</h4>
                            <p className="text-xs text-pf-text-secondary bg-pf-bg-0 p-2 rounded whitespace-pre-wrap break-words font-mono">
                              {log.metadata}
                            </p>
                          </div>
                        )}
                        {visibleColumns.exception && log.exception && (
                          <div>
                            <h4 className="text-xs font-semibold text-pf-text-primary mb-1">Exception Details</h4>
                            <p className="text-xs text-red-400 bg-pf-bg-0 p-2 rounded whitespace-pre-wrap break-words font-mono overflow-y-auto max-h-48">
                              {log.exception}
                            </p>
                          </div>
                        )}
                      </div>
                    </td>
                  </tr>
                )}
              </React.Fragment>
            ))}
            {logs.length === 0 && !loading && (
              <tr><td colSpan={1 + visibleColumnCount} className="p-4 text-center text-pf-text-muted">No logs found</td></tr>
            )}
          </tbody>
        </table>
      </div>
    </PageTemplate>
  );
}
