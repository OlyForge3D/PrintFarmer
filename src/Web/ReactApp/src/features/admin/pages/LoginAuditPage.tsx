import { useMemo } from 'react';
import clsx from 'clsx';
import { PageTemplate } from '@/common/components/PageTemplate';
import {
  Button,
  Input,
  Select,
  FormField,
  Badge,
  Spinner,
  EmptyState,
  Tooltip,
  Table,
  TableHead,
  TableBody,
  TableRow,
  TableHeaderCell,
  TableCell,
} from '@/common/components/ui';
import { ShieldIcon, RefreshIcon, CloseIcon } from '@/common/components/icons/MdiIcons';
import { useUrlFilterState } from '@/common/hooks/useUrlFilterState';
import { useLoginAudit } from '@/features/admin/hooks/useLoginAudit';

const UA_MAX_CHARS = 30;
const PAGE_SIZE_OPTIONS = [25, 50, 100, 200] as const;

const FILTER_CONFIG = {
  from: { key: 'from', type: 'string' as const, defaultValue: '' },
  to: { key: 'to', type: 'string' as const, defaultValue: '' },
  username: { key: 'username', type: 'string' as const, defaultValue: '', debounce: 400 },
  success: { key: 'success', type: 'string' as const, defaultValue: '' },
  page: { key: 'page', type: 'number' as const, defaultValue: 1, filterable: false },
  pageSize: { key: 'pageSize', type: 'number' as const, defaultValue: 50, filterable: false },
};

function formatLocalDateTime(iso: string): string {
  try {
    return new Date(iso).toLocaleString(undefined, {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    });
  } catch {
    return iso;
  }
}

function truncateUserAgent(ua: string): string {
  if (!ua) return '—';
  return ua.length > UA_MAX_CHARS ? `${ua.slice(0, UA_MAX_CHARS)}…` : ua;
}

export function LoginAuditPage() {
  const {
    from, to, username, success, page, pageSize,
    setUsername,
    setMany, resetAll, hasActiveFilters,
  } = useUrlFilterState(FILTER_CONFIG);

  const auditFilters = useMemo(
    () => ({
      from: from || undefined,
      to: to || undefined,
      username: username || undefined,
      success: success === 'true' ? true : success === 'false' ? false : undefined,
      page: page as number,
      pageSize: pageSize as number,
    }),
    [from, to, username, success, page, pageSize],
  );

  const { data, isLoading, isError, error, isFetching } = useLoginAudit(auditFilters);

  const currentPage = (page as number);
  const currentPageSize = (pageSize as number);
  const totalCount = data?.totalCount ?? 0;
  const totalPages = totalCount > 0 ? Math.ceil(totalCount / currentPageSize) : 0;
  const rangeStart = totalCount === 0 ? 0 : (currentPage - 1) * currentPageSize + 1;
  const rangeEnd = Math.min(currentPage * currentPageSize, totalCount);

  const handleFilterWithPageReset = (updates: Record<string, string | number | boolean>) => {
    setMany({ ...updates, page: 1 } as Parameters<typeof setMany>[0]);
  };

  return (
    <PageTemplate
      title="Login Audit Log"
      subtitle="View all authentication attempts with timestamps, outcomes, and originating IP addresses"
      icon={ShieldIcon}
    >
      {/* Filter bar */}
      <div
        className="bg-pf-bg-1 border border-pf-border rounded-md p-4 mb-4"
        role="search"
        aria-label="Filter login audit entries"
      >
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-3 items-end">
          <FormField label="From" htmlFor="audit-from">
            <Input
              id="audit-from"
              type="datetime-local"
              value={from as string}
              onChange={(e) => handleFilterWithPageReset({ from: e.target.value })}
              aria-label="Filter from date and time"
            />
          </FormField>

          <FormField label="To" htmlFor="audit-to">
            <Input
              id="audit-to"
              type="datetime-local"
              value={to as string}
              onChange={(e) => handleFilterWithPageReset({ to: e.target.value })}
              aria-label="Filter to date and time"
            />
          </FormField>

          <FormField label="Username" htmlFor="audit-username">
            <Input
              id="audit-username"
              type="text"
              value={username as string}
              onChange={(e) => setUsername(e.target.value)}
              placeholder="Partial match…"
              aria-label="Filter by username"
            />
          </FormField>

          <FormField label="Status" htmlFor="audit-success">
            <Select
              id="audit-success"
              value={success as string}
              onChange={(e) => handleFilterWithPageReset({ success: e.target.value })}
              aria-label="Filter by login status"
            >
              <option value="">All attempts</option>
              <option value="true">Successful</option>
              <option value="false">Failed</option>
            </Select>
          </FormField>
        </div>

        {hasActiveFilters && (
          <div className="mt-3 flex justify-end">
            <Button
              variant="ghost"
              size="sm"
              onClick={resetAll}
              iconLeft={<CloseIcon className="w-3.5 h-3.5" />}
              aria-label="Clear all filters"
            >
              Clear filters
            </Button>
          </div>
        )}
      </div>

      {/* Table area */}
      {isLoading ? (
        <div className="flex justify-center py-12" role="status" aria-label="Loading login audit entries">
          <Spinner size="lg" />
        </div>
      ) : isError ? (
        <div
          className="p-4 rounded-md bg-pf-error-bg border border-pf-error/30 text-pf-error-text"
          role="alert"
        >
          Failed to load login audit log: {String(error)}
        </div>
      ) : !data?.items.length ? (
        <EmptyState
          title="No login attempts found"
          description={hasActiveFilters ? 'Try adjusting your filters.' : 'No login attempts have been recorded yet.'}
        />
      ) : (
        <>
          {/* Summary + page-size selector */}
          <div className="flex items-center justify-between mb-3 gap-2 flex-wrap">
            <p
              className={clsx('text-sm text-pf-text-secondary', isFetching && 'opacity-60')}
              aria-live="polite"
              aria-atomic="true"
            >
              {isFetching
                ? 'Refreshing…'
                : `Showing ${rangeStart}–${rangeEnd} of ${totalCount.toLocaleString()} entries`}
            </p>

            <div className="flex items-center gap-2">
              {isFetching && <RefreshIcon className="w-3.5 h-3.5 pf-animate-spin text-pf-text-tertiary" aria-hidden />}
              <label htmlFor="audit-page-size" className="text-sm text-pf-text-secondary sr-only">
                Rows per page
              </label>
              <Select
                id="audit-page-size"
                value={String(currentPageSize)}
                onChange={(e) => setMany({ pageSize: Number(e.target.value), page: 1 } as Parameters<typeof setMany>[0])}
                containerClassName="w-28"
                aria-label="Rows per page"
              >
                {PAGE_SIZE_OPTIONS.map((n) => (
                  <option key={n} value={String(n)}>
                    {n} per page
                  </option>
                ))}
              </Select>
            </div>
          </div>

          <div className="overflow-x-auto rounded-md border border-pf-border">
            <Table aria-label="Login audit log">
              <TableHead>
                <TableRow>
                  <TableHeaderCell scope="col">Timestamp</TableHeaderCell>
                  <TableHeaderCell scope="col">Username</TableHeaderCell>
                  <TableHeaderCell scope="col">Status</TableHeaderCell>
                  <TableHeaderCell scope="col" className="font-mono">IP Address</TableHeaderCell>
                  <TableHeaderCell scope="col">Failure Reason</TableHeaderCell>
                  <TableHeaderCell scope="col">User Agent</TableHeaderCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {data.items.map((entry) => (
                  <TableRow
                    key={entry.id}
                    className={clsx(!entry.success && 'bg-pf-error-bg/30')}
                  >
                    <TableCell>
                      <Tooltip content={entry.timestamp} position="right">
                        <span className="text-sm text-pf-text-primary whitespace-nowrap">
                          {formatLocalDateTime(entry.timestamp)}
                        </span>
                      </Tooltip>
                    </TableCell>

                    <TableCell>
                      <span className="text-sm text-pf-text-primary font-medium">
                        {entry.username}
                      </span>
                    </TableCell>

                    <TableCell>
                      {entry.success ? (
                        <Badge variant="success">
                          ✅ Success
                        </Badge>
                      ) : (
                        <Badge variant="error">
                          ❌ Failed
                        </Badge>
                      )}
                    </TableCell>

                    <TableCell>
                      <span className="font-mono text-sm text-pf-text-primary">
                        {entry.ipAddress || '—'}
                      </span>
                    </TableCell>

                    <TableCell>
                      <span className="text-sm text-pf-text-secondary">
                        {entry.failureReason ?? '—'}
                      </span>
                    </TableCell>

                    <TableCell>
                      {entry.userAgent ? (
                        <Tooltip content={entry.userAgent} position="left">
                          <span
                            className="text-sm text-pf-text-secondary font-mono cursor-default"
                            aria-label={`User agent: ${entry.userAgent}`}
                          >
                            {truncateUserAgent(entry.userAgent)}
                          </span>
                        </Tooltip>
                      ) : (
                        <span className="text-sm text-pf-text-tertiary">—</span>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>

          {/* Pagination controls */}
          {totalPages > 1 && (
            <nav
              className="flex items-center justify-between mt-4 gap-2 flex-wrap"
              aria-label="Pagination"
            >
              <p className="text-sm text-pf-text-secondary">
                Page {currentPage} of {totalPages}
              </p>
              <div className="flex items-center gap-2">
                <Button
                  variant="secondary"
                  size="sm"
                  disabled={currentPage <= 1}
                  onClick={() => setMany({ page: currentPage - 1 } as Parameters<typeof setMany>[0])}
                  aria-label="Previous page"
                >
                  Previous
                </Button>
                <Button
                  variant="secondary"
                  size="sm"
                  disabled={currentPage >= totalPages}
                  onClick={() => setMany({ page: currentPage + 1 } as Parameters<typeof setMany>[0])}
                  aria-label="Next page"
                >
                  Next
                </Button>
              </div>
            </nav>
          )}
        </>
      )}
    </PageTemplate>
  );
}
