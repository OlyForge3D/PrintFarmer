import React, { useState } from "react";
import axios from "axios";

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

export default function LogsPage() {
  const [logs, setLogs] = useState<SystemLog[]>([]);
  const [loading, setLoading] = useState(false);
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
      const params: Record<string, string> = {};
      if (filters.correlationId) params.correlationId = filters.correlationId;
      if (filters.level) params.level = filters.level;
      if (filters.from) params.from = filters.from;
      if (filters.to) params.to = filters.to;
      if (filters.metadata) params.metadata = filters.metadata;
      const res = await axios.get("/api/systemlogs", { params });
      setLogs(res.data);
    } catch {
      alert("Failed to fetch logs");
    }
    setLoading(false);
  };

  const exportLogs = async () => {
    try {
      const params: Record<string, string> = {};
      if (filters.correlationId) params.correlationId = filters.correlationId;
      if (filters.level) params.level = filters.level;
      if (filters.from) params.from = filters.from;
      if (filters.to) params.to = filters.to;
      if (filters.metadata) params.metadata = filters.metadata;
      const res = await axios.get("/api/systemlogs/export", { params, responseType: "blob" });
      const url = window.URL.createObjectURL(new Blob([res.data]));
      const link = document.createElement("a");
      link.href = url;
      link.setAttribute("download", `systemlogs_export.json`);
      document.body.appendChild(link);
      link.click();
      link.remove();
    } catch {
      alert("Failed to export logs");
    }
  };

  return (
    <div className="p-6 max-w-6xl mx-auto">
      <h1 className="text-2xl font-bold mb-4">System Logs</h1>
      <div className="mb-4 grid grid-cols-1 md:grid-cols-5 gap-2">
        <input className="border p-2" placeholder="CorrelationId" value={filters.correlationId} onChange={e => setFilters(f => ({ ...f, correlationId: e.target.value }))} />
        <input className="border p-2" placeholder="Level" value={filters.level} onChange={e => setFilters(f => ({ ...f, level: e.target.value }))} />
        <input className="border p-2" type="date" placeholder="From" value={filters.from} onChange={e => setFilters(f => ({ ...f, from: e.target.value }))} />
        <input className="border p-2" type="date" placeholder="To" value={filters.to} onChange={e => setFilters(f => ({ ...f, to: e.target.value }))} />
        <input className="border p-2" placeholder="Metadata" value={filters.metadata} onChange={e => setFilters(f => ({ ...f, metadata: e.target.value }))} />
      </div>
      <div className="mb-4 flex gap-2">
        <button className="bg-blue-600 text-white px-4 py-2 rounded" onClick={fetchLogs} disabled={loading}>Search</button>
        <button className="bg-green-600 text-white px-4 py-2 rounded" onClick={exportLogs}>Export</button>
      </div>
      <div className="overflow-x-auto">
        <table className="min-w-full border">
          <thead>
            <tr className="bg-gray-100">
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
    </div>
  );
}
