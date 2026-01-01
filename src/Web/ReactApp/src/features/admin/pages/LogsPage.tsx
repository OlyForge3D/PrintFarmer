import React, { useState } from "react";
import axios from "axios";
import { Button, Input, FormField, Select } from '@/common/components/ui';
import { PageTemplate } from '@/common/components/PageTemplate';

interface SystemLog {
  id: number;
  timestamp: string;
  level: string;
  message: string;
  exception?: string;
  source?: string;
  correlationId?: string;
  metadata?: string;
}

export function LogsPage() {
  const [logs, setLogs] = useState<SystemLog[]>([]);
  const [loading, setLoading] = useState(false);
  const [useAdvancedQuery, setUseAdvancedQuery] = useState(false);
  const [queryString, setQueryString] = useState("");
  const [filters, setFilters] = useState({
    correlationId: "",
    level: "",
    from: "",
    to: "",
    metadata: ""
  });

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

  return (
    <PageTemplate title="System Logs">
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

      {useAdvancedQuery && (
        <div className="mb-4">
          <FormField label="Lucene Query" inline={false}>
            <textarea
              value={queryString}
              onChange={e => setQueryString(e.target.value)}
              placeholder="Example: level:Error AND message:timeout"
              className="w-full p-2 border rounded bg-pf-bg-0 text-pf-text-primary border-pf-border focus:outline-none focus:ring-2 focus:ring-pf-accent"
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

      <div className="mb-4 flex gap-2">
        <Button variant="primary" onClick={fetchLogs} disabled={loading}>Search</Button>
        <Button variant="success" onClick={exportLogs}>Export</Button>
      </div>
      {loading && <div className="text-pf-text-muted mb-4">Loading logs...</div>}
      <div className="overflow-x-auto">
        <table className="min-w-full border">
          <thead>
            <tr className="bg-pf-bg-1">
              <th className="p-2 border">Timestamp</th>
              <th className="p-2 border">Level</th>
              <th className="p-2 border">Message</th>
              <th className="p-2 border">CorrelationId</th>
              <th className="p-2 border">Metadata</th>
              <th className="p-2 border">Exception</th>
              <th className="p-2 border">Source</th>
            </tr>
          </thead>
          <tbody>
            {logs.map(log => (
              <tr key={log.id}>
                <td className="p-2 border whitespace-nowrap">{new Date(log.timestamp).toLocaleString()}</td>
                <td className="p-2 border">{log.level}</td>
                <td className="p-2 border max-w-xs truncate" title={log.message}>{log.message}</td>
                <td className="p-2 border">{log.correlationId}</td>
                <td className="p-2 border max-w-xs truncate" title={log.metadata}>{log.metadata}</td>
                <td className="p-2 border max-w-xs truncate" title={log.exception}>{log.exception}</td>
                <td className="p-2 border">{log.source}</td>
              </tr>
            ))}
            {logs.length === 0 && !loading && (
              <tr><td colSpan={7} className="p-2 text-center">No logs found</td></tr>
            )}
          </tbody>
        </table>
      </div>
    </PageTemplate>
  );
}
