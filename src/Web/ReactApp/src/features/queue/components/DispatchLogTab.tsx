import { useCallback, useEffect, useState } from "react";
import { Alert } from "@/common/components/ui/Alert";
import { Badge } from "@/common/components/ui/Badge";
import { Button } from "@/common/components/ui/Button";
import { Spinner } from "@/common/components/ui/Spinner";
import { apiClient } from "@/services/api";
import type { DispatchHistoryDto, DispatchHistoryPageDto } from "@/types/api";

const PAGE_SIZE = 25;

function actionBadgeVariant(action: string): "success" | "default" | "warning" | "error" {
  switch (action) {
    case "Dispatched": return "success";
    case "Suggested": return "default";
    case "Rejected": return "warning";
    case "Failed": return "error";
    default: return "default";
  }
}

function formatTimestamp(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleString(undefined, {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

export default function DispatchLogTab() {
  const [items, setItems] = useState<DispatchHistoryDto[]>([]);
  const [totalCount, setTotalCount] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const totalPages = Math.max(1, Math.ceil(totalCount / PAGE_SIZE));

  const fetchData = useCallback(async (p: number) => {
    setLoading(true);
    setError(null);
    try {
      const data: DispatchHistoryPageDto = await apiClient.getDispatchHistory(p, PAGE_SIZE);
      setItems(data.items);
      setTotalCount(data.totalCount);
      setPage(data.page);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load dispatch history");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchData(page);
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const handlePrev = () => {
    if (page > 1) fetchData(page - 1);
  };
  const handleNext = () => {
    if (page < totalPages) fetchData(page + 1);
  };

  if (loading && items.length === 0) {
    return (
      <div className="flex justify-center items-center p-12">
        <Spinner size="lg" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="p-4">
        <Alert variant="error" title="Error loading dispatch log">
          {error}
        </Alert>
      </div>
    );
  }

  if (totalCount === 0 && !loading) {
    return (
      <div className="p-8 text-center text-pf-text-secondary">
        No dispatch history yet. Jobs will appear here when auto-dispatch sends files to printers.
      </div>
    );
  }

  return (
    <div className="flex flex-col h-full">
      {/* Summary bar */}
      <div className="shrink-0 p-4 border-b border-pf-border bg-pf-bg-1 flex items-center justify-between">
        <span className="text-sm text-pf-text-secondary">
          {totalCount} dispatch event{totalCount !== 1 ? "s" : ""}
        </span>
        <Button variant="ghost" size="sm" onClick={() => fetchData(page)} loading={loading}>
          Refresh
        </Button>
      </div>

      {/* Table */}
      <div className="flex-1 overflow-auto bg-pf-bg-1">
        <table className="w-full text-sm">
          <thead className="sticky top-0 bg-pf-bg-2 border-b border-pf-border">
            <tr>
              <th className="text-left px-4 py-2 font-medium text-pf-text-secondary">Time</th>
              <th className="text-left px-4 py-2 font-medium text-pf-text-secondary">Job</th>
              <th className="text-left px-4 py-2 font-medium text-pf-text-secondary">Printer</th>
              <th className="text-left px-4 py-2 font-medium text-pf-text-secondary">Action</th>
              <th className="text-right px-4 py-2 font-medium text-pf-text-secondary">Score</th>
              <th className="text-left px-4 py-2 font-medium text-pf-text-secondary">Reason</th>
            </tr>
          </thead>
          <tbody>
            {items.map((row) => (
              <tr key={row.id} className="border-b border-pf-border/50 hover:bg-pf-bg-2/50">
                <td className="px-4 py-2 whitespace-nowrap text-pf-text-secondary">
                  {formatTimestamp(row.createdAtUtc)}
                </td>
                <td className="px-4 py-2 text-pf-text-primary truncate max-w-[200px]" title={row.jobName ?? ""}>
                  {row.jobName ?? "—"}
                </td>
                <td className="px-4 py-2 text-pf-text-primary truncate max-w-[160px]" title={row.printerName ?? ""}>
                  {row.printerName ?? "—"}
                </td>
                <td className="px-4 py-2">
                  <Badge variant={actionBadgeVariant(row.action)} size="sm">
                    {row.action}
                  </Badge>
                </td>
                <td className="px-4 py-2 text-right tabular-nums text-pf-text-secondary">
                  {row.score != null ? row.score.toFixed(1) : "—"}
                </td>
                <td className="px-4 py-2 text-pf-text-secondary truncate max-w-[240px]" title={row.reason ?? ""}>
                  {row.reason ?? "—"}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {/* Pagination */}
      {totalPages > 1 && (
        <div className="shrink-0 p-3 border-t border-pf-border bg-pf-bg-1 flex items-center justify-between">
          <Button variant="ghost" size="sm" onClick={handlePrev} disabled={page <= 1}>
            ← Previous
          </Button>
          <span className="text-sm text-pf-text-secondary">
            Page {page} of {totalPages}
          </span>
          <Button variant="ghost" size="sm" onClick={handleNext} disabled={page >= totalPages}>
            Next →
          </Button>
        </div>
      )}
    </div>
  );
}
